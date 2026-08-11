using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Construye un arbol de tags de prueba y copia sus valores dummy a los nodos.
/// La jerarquia sale de los puntos en el nombre del tag: cada segmento
/// intermedio es una carpeta, el ultimo segmento es la variable.
public class GatewayNodeManager : CustomNodeManager2
{
    // Cada cuantos ciclos de UpdateValues se simula un cambio de valor.
    private const int SimulationIntervalCycles = 5;
    private const string SimulatedTagName = "PLANTA_01.MEDICION.PRESION_ENTRADA";
    private const double SimulationStep = 0.1;

    // Tags hardcodeados hasta que exista un cargador de CSV real. El valor y su
    // SourceTimestamp viajan siempre juntos: el timestamp solo se mueve cuando
    // el valor efectivamente cambia (ver SimulateChanges), nunca en cada ciclo
    // de publicacion.
    private readonly Dictionary<string, (object Value, DateTime SourceTimestamp)> _tags;

    // Cada nodo apareado con el nombre del tag que lo alimenta. Se arma una vez,
    // al construir el arbol, para no resolver nombres en cada ciclo.
    private readonly List<(BaseDataVariableState Node, string TagName)> _bindings = new();

    // Carpetas ya creadas, indexadas por su ruta de segmentos, para que dos tags
    // que comparten un tramo del nombre no dupliquen la carpeta intermedia.
    private readonly Dictionary<string, FolderState> _folders = new();

    private int _cycleCount;

    public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        string namespaceUri)
        : base(server, configuration, namespaceUri)
    {
        var startup = DateTime.UtcNow;
        _tags = new Dictionary<string, (object Value, DateTime SourceTimestamp)>
        {
            ["PLANTA_01.MEDICION.PRESION_ENTRADA"] = (4.21, startup),
            ["PLANTA_01.MEDICION.CAUDAL.TOTALIZADO"] = (128.5, startup),
            ["PLANTA_01.ESTADO.BOMBA_01"] = (true, startup)
        };
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

            foreach (var (tagName, tag) in _tags)
            {
                AddTag(root, tagName, tag.Value, tag.SourceTimestamp);
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
    /// suma un paso fijo a SimulatedTagName y recien ahi mueve su
    /// SourceTimestamp. Los otros dos tags no se tocan nunca: quedan clavados
    /// en el timestamp de arranque a proposito, como contraste que demuestra
    /// que UpdateValues no pisa el timestamp en cada publicacion.
    private void SimulateChanges()
    {
        _cycleCount++;
        if (_cycleCount % SimulationIntervalCycles != 0) return;

        var current = (double)_tags[SimulatedTagName].Value;
        _tags[SimulatedTagName] = (current + SimulationStep, DateTime.UtcNow);
    }

    // ---------- Construccion del arbol ----------

    /// Agrega un tag al arbol, creando las carpetas intermedias que hagan
    /// falta segun los segmentos separados por punto en su nombre.
    private void AddTag(NodeState root, string tagName, object value, DateTime sourceTimestamp)
    {
        var segments = tagName.Split('.');
        NodeState parent = root;
        var path = "";

        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            parent = GetOrAddFolder(parent, path, segments[i]);
        }

        var variable = CreateVariable(parent, tagName, segments[^1], value, sourceTimestamp);
        parent.AddChild(variable);
        _bindings.Add((variable, tagName));
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
        object value, DateTime sourceTimestamp)
    {
        var dataType = value switch
        {
            bool => DataTypeIds.Boolean,
            double => DataTypeIds.Double,
            _ => throw new InvalidOperationException(
                $"Tipo de dato no soportado para '{tagName}': {value.GetType()}")
        };

        return new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(tagName, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = sourceTimestamp
        };
    }

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
