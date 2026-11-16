using Parser.Models;
using Parser.Repositories;

namespace Interpreter.Services;

/// <summary>
/// Represents metadata about a discovered VHDL entity.
/// </summary>
public class EntityInfo
{
    /// <summary>
    /// Gets or sets the entity name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file path where the entity is defined.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the architecture name (if parsed).
    /// </summary>
    public string? ArchitectureName { get; set; }

    /// <summary>
    /// Gets or sets the generic parameters of the entity.
    /// </summary>
    public HashSet<Variable> Generics { get; set; } = new();

    /// <summary>
    /// Gets or sets the ports of the entity.
    /// </summary>
    public HashSet<Variable> Ports { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this entity has been fully parsed.
    /// </summary>
    public bool IsParsed { get; set; } = false;

    /// <summary>
    /// Gets or sets the raw token stream for deferred parsing.
    /// </summary>
    public TokenStream? TokenStream { get; set; }

    /// <summary>Diagnostic-friendly representation of the entity record.</summary>
    public override string ToString() => $"Entity '{Name}' from {Path.GetFileName(FilePath)} ({Ports.Count} ports, {Generics.Count} generics)";
}

/// <summary>
/// Represents a component instantiation (port map) in a design.
/// </summary>
public class ComponentInstance
{
    /// <summary>
    /// Gets or sets the instance label/name.
    /// </summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entity name being instantiated.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name (default is "work").
    /// </summary>
    public string LibraryName { get; set; } = "work";

    /// <summary>
    /// Gets or sets the generic map associations.
    /// Key is the generic parameter name, Value is the actual value/expression.
    /// </summary>
    public Dictionary<string, object> GenericMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the port map associations.
    /// Key is the port name, Value is the connected signal name or expression.
    /// </summary>
    public Dictionary<string, string> PortMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the resolved entity info (after lookup).
    /// </summary>
    public EntityInfo? ResolvedEntity { get; set; }

    /// <summary>Diagnostic-friendly representation of the component instance.</summary>
    public override string ToString() => $"{InstanceName}: {LibraryName}.{EntityName}";
}

/// <summary>
/// Registry for discovering and managing VHDL entities across multiple files.
/// Provides functionality similar to FPGA tools (Quartus, Vivado) for building design hierarchies.
/// v0.16.0: Added to support multi-file hierarchical designs.
/// </summary>
public class EntityRegistry
{
    private static EntityRegistry? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of the EntityRegistry.
    /// </summary>
    public static EntityRegistry Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new EntityRegistry();
                }
            }
            return _instance;
        }
    }

    private readonly Dictionary<string, EntityInfo> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ComponentInstance>> _componentInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scannedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all registered entities.
    /// </summary>
    public IReadOnlyDictionary<string, EntityInfo> Entities => _entities;

    /// <summary>
    /// Gets all component instances by parent entity.
    /// </summary>
    public IReadOnlyDictionary<string, List<ComponentInstance>> ComponentInstances => _componentInstances;

    /// <summary>
    /// Scans a single VHDL file for entity definitions.
    /// </summary>
    /// <param name="filePath">The file to scan.</param>
    /// <returns>The number of entities found.</returns>
    public int ScanFile(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        if (_scannedFiles.Contains(filePath))
            return 0;

        _scannedFiles.Add(filePath);

        string[] lines = File.ReadAllLines(filePath);
        var tokenizer = new Tokenizer();

        // Tokenize each line
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                tokenizer.TokenizeLine(line);
        }
        tokenizer.Evaluate();

        var tokens = tokenizer.GetDictionaryType();
        var stream = new TokenStream(tokens);

        int entitiesFound = 0;

        while (stream.Current != null)
        {
            if (stream.Current.Value.ToLower() == "entity")
            {
                stream.SetRollBack();
                stream.MoveNext(); // skip 'entity' keyword

                // Check if next token is an identifier (entity name)
                if (stream.Current != null && stream.Current.Type == Constants.TYPE.IDENTIFIERS)
                {
                    string potentialName = stream.Current.Value;
                    stream.MoveNext();

                    // Check if this is followed by 'is' (entity declaration) or '.' (component instantiation)
                    if (stream.Current != null && stream.Current.Value.ToLower() == "is")
                    {
                        // This is an entity declaration
                        stream.RollBack(); // go back to 'entity' keyword
                        var entityInfo = ParseEntityDeclaration(stream, filePath);
                        if (entityInfo != null)
                        {
                            RegisterEntity(entityInfo);
                            entitiesFound++;
                        }
                    }
                }
            }
            else
            {
                stream.MoveNext();
            }
        }

        return entitiesFound;
    }

    /// <summary>
    /// Parses an entity declaration from the token stream.
    /// </summary>
    private EntityInfo? ParseEntityDeclaration(TokenStream stream, string filePath)
    {
        // Skip 'entity' keyword
        stream.MoveNext();

        if (stream.Current == null)
            return null;

        var entityInfo = new EntityInfo
        {
            FilePath = filePath,
            Name = stream.Current.Value
        };

        stream.MoveNext(); // skip entity name

        // Skip 'is' keyword
        if (stream.Current?.Value.ToLower() == "is")
            stream.MoveNext();

        // Parse generics and ports
        while (stream.Current != null)
        {
            string currentVal = stream.Current.Value.ToLower();

            if (currentVal == "generic")
            {
                stream.MoveNext(); // skip 'generic'
                if (stream.Current?.Value == "(")
                {
                    entityInfo.Generics = ParsePortOrGenericList(stream, isGeneric: true);
                }
            }
            else if (currentVal == "port")
            {
                stream.MoveNext(); // skip 'port'
                if (stream.Current?.Value == "(")
                {
                    entityInfo.Ports = ParsePortOrGenericList(stream, isGeneric: false);
                }
            }
            else if (currentVal == "end")
            {
                // End of entity declaration
                SkipToSemicolon(stream);
                break;
            }
            else
            {
                stream.MoveNext();
            }
        }

        entityInfo.IsParsed = true;
        return entityInfo;
    }

    /// <summary>
    /// Parses a port or generic list from parentheses.
    /// </summary>
    private HashSet<Variable> ParsePortOrGenericList(TokenStream stream, bool isGeneric)
    {
        var result = new HashSet<Variable>();

        if (stream.Current?.Value != "(")
            return result;

        stream.MoveNext(); // skip '('

        int parenDepth = 1;
        var currentNames = new List<string>();
        bool expectingType = false;
        Constants.PortType portType = Constants.PortType.IN;
        Constants.DATATYPES dataType = Constants.DATATYPES.STD_LOGIC;
        int size = 1;

        while (stream.Current != null && parenDepth > 0)
        {
            string val = stream.Current.Value;
            string valLower = val.ToLower();

            if (val == "(")
            {
                parenDepth++;
                stream.MoveNext();
                // Skip nested parentheses (type ranges)
                while (stream.Current != null && parenDepth > 1)
                {
                    if (stream.Current.Value == "(") parenDepth++;
                    else if (stream.Current.Value == ")") parenDepth--;
                    stream.MoveNext();
                }
                continue;
            }
            else if (val == ")")
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    // Create variables for remaining names
                    foreach (var name in currentNames)
                    {
                        result.Add(new Variable
                        {
                            Name = name,
                            Type = dataType,
                            Size = size,
                            IsPort = !isGeneric,
                            PortType = portType
                        });
                    }
                    stream.MoveNext();
                    break;
                }
            }
            else if (val == ":")
            {
                expectingType = true;
            }
            else if (val == ";")
            {
                // End of this port/generic declaration
                foreach (var name in currentNames)
                {
                    result.Add(new Variable
                    {
                        Name = name,
                        Type = dataType,
                        Size = size,
                        IsPort = !isGeneric,
                        PortType = portType
                    });
                }
                currentNames.Clear();
                expectingType = false;
                dataType = Constants.DATATYPES.STD_LOGIC;
                size = 1;
            }
            else if (val == ",")
            {
                // Multiple names
            }
            else if (valLower == "in")
            {
                portType = Constants.PortType.IN;
            }
            else if (valLower == "out")
            {
                portType = Constants.PortType.OUT;
            }
            else if (valLower == "inout")
            {
                portType = Constants.PortType.OUT; // treat as out for now
            }
            else if (expectingType)
            {
                dataType = Variable.GetDatatype(val);
                if (dataType == Constants.DATATYPES.STD_LOGIC_VECTOR)
                    size = 8; // default vector size
                else if (dataType == Constants.DATATYPES.INTEGER)
                    size = 32;
            }
            else if (stream.Current.Type == Constants.TYPE.IDENTIFIERS && !expectingType)
            {
                // This is a port/generic name
                currentNames.Add(val);
            }

            stream.MoveNext();
        }

        return result;
    }

    /// <summary>
    /// Skips tokens until semicolon.
    /// </summary>
    private void SkipToSemicolon(TokenStream stream)
    {
        while (stream.Current != null && stream.Current.Value != ";")
        {
            stream.MoveNext();
        }
        if (stream.Current?.Value == ";")
            stream.MoveNext();
    }

    /// <summary>
    /// Registers an entity in the registry.
    /// </summary>
    public void RegisterEntity(EntityInfo entityInfo)
    {
        _entities[entityInfo.Name] = entityInfo;
    }

    /// <summary>
    /// Looks up an entity by name.
    /// </summary>
    /// <param name="entityName">The entity name to look up.</param>
    /// <returns>The entity info, or null if not found.</returns>
    public EntityInfo? GetEntity(string entityName)
    {
        return _entities.TryGetValue(entityName, out var entity) ? entity : null;
    }

    /// <summary>
    /// Checks if an entity exists.
    /// </summary>
    public bool ContainsEntity(string entityName) => _entities.ContainsKey(entityName);

    /// <summary>
    /// Registers a component instance for a parent entity.
    /// </summary>
    public void RegisterComponentInstance(string parentEntityName, ComponentInstance instance)
    {
        if (!_componentInstances.ContainsKey(parentEntityName))
        {
            _componentInstances[parentEntityName] = new List<ComponentInstance>();
        }
        _componentInstances[parentEntityName].Add(instance);

        // Try to resolve the entity
        instance.ResolvedEntity = GetEntity(instance.EntityName);
    }

    /// <summary>
    /// Gets all component instances for a parent entity.
    /// </summary>
    public List<ComponentInstance> GetComponentInstances(string parentEntityName)
    {
        return _componentInstances.TryGetValue(parentEntityName, out var instances) ? instances : new List<ComponentInstance>();
    }

    /// <summary>
    /// Clears all registered entities and instances.
    /// </summary>
    public void Clear()
    {
        _entities.Clear();
        _componentInstances.Clear();
        _scannedFiles.Clear();
    }

    /// <summary>
    /// Resets the singleton instance (for testing).
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _instance?.Clear();
            _instance = null;
        }
    }
}
