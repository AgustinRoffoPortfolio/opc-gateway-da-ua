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
    // Definiciones en el orden del CSV: de ahi sale el arbol de nodos.
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;

    // Fuente de los valores publicados. El node manager no lee del servidor DA:
    // pide el ultimo estado conocido a su ritmo, sin saber a que frecuencia se
    // llena la cache del otro lado.
    private readonly TagCache _cache;

    // Cada nodo apareado con el nombre del tag que lo alimenta. Se arma una vez,
    // al construir el arbol, para no resolver nombres en cada ciclo.
    private readonly List<(BaseDataVariableState Node, string TagName)> _bindings = new();

    // Carpetas ya creadas, indexadas por su ruta de segmentos, para que dos tags
    // que comparten un tramo del nombre no dupliquen la carpeta intermedia.
    private readonly Dictionary<string, FolderState> _folders = new();

    public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        string namespaceUri, IReadOnlyList<TagDefinition> tagDefinitions, TagCache cache)
        : base(server, configuration, namespaceUri)
    {
        _tagDefinitions = tagDefinitions;
        _cache = cache;
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

            // Los nodos nacen con el estado que la cache ya tiene: antes de la
            // primera lectura DA eso es "esperando dato inicial", no Good. Un
            // cliente que conecta en ese hueco tiene que ver que todavia no hay
            // dato, no un cero con calidad buena.
            // Los tags Hidden se saltean a proposito: el driver DA los sigue
            // leyendo y la cache los mantiene al dia, pero no se crea el nodo
            // UA, asi que ningun cliente los ve. Es distinto de ENABLED=False,
            // que directamente no se lee del servidor DA.
            foreach (var tag in _tagDefinitions)
            {
                if (tag.AccessLevel == TagAccessLevel.Hidden) continue;
                AddTag(root, tag, _cache.Get(tag.OpcUaName));
            }

            AddPredefinedNode(SystemContext, root);
        }
    }

    /// Copia a los nodos el ultimo estado conocido de cada tag y avisa a los
    /// clientes suscriptos. Se llama desde el timer del programa principal.
    public void UpdateValues()
    {
        lock (Lock)
        {
            foreach (var (node, tagName) in _bindings)
                Publish(node, _cache.Get(tagName));
        }
    }

    /// Vuelca un estado de la cache a un nodo UA.
    private void Publish(BaseDataVariableState node, TagState state)
    {
        node.Value = state.ScaledValue;
        node.StatusCode = QualityMapper.ToStatusCode(state.Quality);

        // node.Timestamp es el SourceTimestamp: el momento en que el dato se
        // origino en el servidor DA. El ServerTimestamp lo pone el stack UA al
        // responder, y son cosas distintas. Pisar este con la hora de lectura
        // corrompe el historico de cualquier cosa que este aguas abajo.
        node.Timestamp = state.SourceTimestamp;

        node.ClearChangeMasks(SystemContext, false);
    }

    /// Unico punto que muta _tags. Cada SimulationIntervalCycles llamadas le
    /// suma un paso fijo a _simulatedTagName y recien ahi mueve su
    /// SourceTimestamp. Los demas tags no se tocan nunca: quedan clavados en
    /// el timestamp de arranque a proposito, como contraste que demuestra que
    /// UpdateValues no pisa el timestamp en cada publicacion.

    // ---------- Construccion del arbol ----------

    /// Agrega un tag al arbol, creando las carpetas intermedias que hagan
    /// falta segun los segmentos separados por punto en su nombre.
    private void AddTag(NodeState root, TagDefinition tag, TagState state)
    {
        var segments = tag.OpcUaName.Split('.');
        NodeState parent = root;
        var path = "";

        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            parent = GetOrAddFolder(parent, path, segments[i]);
        }

        var variable = CreateVariable(parent, tag.OpcUaName, segments[^1], tag.DataType, state);
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
        TagDataType dataType, TagState state)
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
            Value = state.ScaledValue,
            StatusCode = QualityMapper.ToStatusCode(state.Quality),
            Timestamp = state.SourceTimestamp
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
