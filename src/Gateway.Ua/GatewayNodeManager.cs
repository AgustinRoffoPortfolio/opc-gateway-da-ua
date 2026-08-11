using Gateway.Core;
using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Construye el arbol de tags a partir de las TagDefinition leidas del CSV y
/// copia sus valores dummy a los nodos. La jerarquia sale de los puntos en
/// el nombre del tag: cada segmento intermedio es una carpeta, el ultimo
/// segmento es la variable.
public class GatewayNodeManager : CustomNodeManager2
{
    // Cada cuantos ciclos de UpdateValues se simula un cambio de valor.
    private const int SimulationIntervalCycles = 5;
    private const double SimulationStep = 0.1;

    // Definiciones en el orden del CSV: de ahi sale tanto el arbol como el
    // tag elegido para la simulacion ("el primero numerico de la lista").
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;

    // Nombre del tag que SimulateChanges mueve, o null si el CSV no trae
    // ningun tag numerico. Se resuelve una sola vez, al construir.
    private readonly string? _simulatedTagName;

    // El valor y su SourceTimestamp viajan siempre juntos: el timestamp solo
    // se mueve cuando el valor efectivamente cambia (ver SimulateChanges),
    // nunca en cada ciclo de publicacion.
    private readonly Dictionary<string, (object Value, DateTime SourceTimestamp)> _tags;

    // Cada nodo apareado con el nombre del tag que lo alimenta. Se arma una vez,
    // al construir el arbol, para no resolver nombres en cada ciclo.
    private readonly List<(BaseDataVariableState Node, string TagName)> _bindings = new();

    // Carpetas ya creadas, indexadas por su ruta de segmentos, para que dos tags
    // que comparten un tramo del nombre no dupliquen la carpeta intermedia.
    private readonly Dictionary<string, FolderState> _folders = new();

    private int _cycleCount;

    public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        string namespaceUri, IReadOnlyList<TagDefinition> tagDefinitions)
        : base(server, configuration, namespaceUri)
    {
        _tagDefinitions = tagDefinitions;

        var startup = DateTime.UtcNow;
        _tags = tagDefinitions.ToDictionary(
            tag => tag.OpcUaName,
            tag => (DefaultValue(tag.DataType), startup));

        _simulatedTagName = tagDefinitions
            .FirstOrDefault(tag => tag.DataType is TagDataType.Double or TagDataType.Int32)
            ?.OpcUaName;
    }

    /// Cantidad de tags publicados. La usa el arranque para loguear.
    public int TagCount => _bindings.Count;

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var root = new FolderState(null)
            {
                NodeId = new NodeId("Gateway", NamespaceIndex),
                BrowseName = new QualifiedName("Gateway", NamespaceIndex),
                DisplayName = "Gateway",
                TypeDefinitionId = ObjectTypeIds.FolderType
            };
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            LinkToParent(externalReferences, ObjectIds.ObjectsFolder,
                root.NodeId, ReferenceTypeIds.Organizes);

            foreach (var tag in _tagDefinitions)
            {
                var (value, sourceTimestamp) = _tags[tag.OpcUaName];
                AddTag(root, tag, value, sourceTimestamp);
            }

            AddPredefinedNode(SystemContext, root);
        }
    }

    /// Copia los valores actuales de _tags a los nodos y avisa a los clientes
    /// suscriptos. Se llama desde el timer del programa principal.
    public void UpdateValues()
    {
        lock (Lock)
        {
            SimulateChanges();

            foreach (var (node, tagName) in _bindings)
            {
                var (value, sourceTimestamp) = _tags[tagName];
                node.Value = value;
                node.StatusCode = StatusCodes.Good;
                node.Timestamp = sourceTimestamp;
                node.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    /// Unico punto que muta _tags. Cada SimulationIntervalCycles llamadas le
    /// suma un paso fijo a _simulatedTagName y recien ahi mueve su
    /// SourceTimestamp. Los demas tags no se tocan nunca: quedan clavados en
    /// el timestamp de arranque a proposito, como contraste que demuestra que
    /// UpdateValues no pisa el timestamp en cada publicacion.
    private void SimulateChanges()
    {
        _cycleCount++;
        if (_cycleCount % SimulationIntervalCycles != 0) return;
        if (_simulatedTagName is null) return;

        var (value, _) = _tags[_simulatedTagName];
        var next = value switch
        {
            double d => (object)(d + SimulationStep),
            int i => i + 1,
            _ => value
        };
        _tags[_simulatedTagName] = (next, DateTime.UtcNow);
    }

    // ---------- Construccion del arbol ----------

    /// Agrega un tag al arbol, creando las carpetas intermedias que hagan
    /// falta segun los segmentos separados por punto en su nombre.
    private void AddTag(NodeState root, TagDefinition tag, object value, DateTime sourceTimestamp)
    {
        var segments = tag.OpcUaName.Split('.');
        NodeState parent = root;
        var path = "";

        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            parent = GetOrAddFolder(parent, path, segments[i]);
        }

        var variable = CreateVariable(parent, tag.OpcUaName, segments[^1], tag.DataType, value, sourceTimestamp);
        parent.AddChild(variable);
        _bindings.Add((variable, tag.OpcUaName));
    }

    /// Devuelve la carpeta para esa ruta, creandola la primera vez que se pide.
    private FolderState GetOrAddFolder(NodeState parent, string path, string name)
    {
        if (_folders.TryGetValue(path, out var existing)) return existing;

        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            ReferenceTypeId = ReferenceTypeIds.Organizes
        };
        parent.AddChild(folder);
        _folders[path] = folder;
        return folder;
    }

    /// Variable simple, sin unidad de ingenieria ni rango: el CSV todavia no los trae.
    private BaseDataVariableState CreateVariable(NodeState parent, string tagName, string name,
        TagDataType dataType, object value, DateTime sourceTimestamp)
    {
        var uaDataType = dataType switch
        {
            TagDataType.Double => DataTypeIds.Double,
            TagDataType.Boolean => DataTypeIds.Boolean,
            TagDataType.Int32 => DataTypeIds.Int32,
            TagDataType.String => DataTypeIds.String,
            _ => throw new InvalidOperationException(
                $"Tipo de dato no soportado para '{tagName}': {dataType}")
        };

        return new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(tagName, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = uaDataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = sourceTimestamp
        };
    }

    /// Valor dummy inicial, uno coherente por tipo de dato.
    private static object DefaultValue(TagDataType dataType) => dataType switch
    {
        TagDataType.Double => 0.0,
        TagDataType.Boolean => false,
        TagDataType.Int32 => 0,
        TagDataType.String => "",
        _ => throw new InvalidOperationException($"Tipo de dato no soportado: {dataType}")
    };

    /// Registra la referencia inversa desde un nodo que no es nuestro.
    private static void LinkToParent(IDictionary<NodeId, IList<IReference>> externalReferences,
        NodeId parentId, NodeId childId, NodeId referenceType)
    {
        if (!externalReferences.TryGetValue(parentId, out IList<IReference>? refs))
        {
            externalReferences[parentId] = refs = new List<IReference>();
        }
        refs.Add(new NodeStateReference(referenceType, false, childId));
    }
}
