        require.config({ paths: { vs: 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs' } });
        
        var editor;
        var tabModels = new Map();     // Key: tabId (string), Value: monaco.editor.ITextModel
        var tabViewStates = new Map(); // Key: tabId (string), Value: monaco.editor.ICodeEditorViewState
        var activeTabId = null;
        var tableColumns = {}; // Key: "schema.table" & "table", Value: ["col1", "col2", ...]
        var schemas = [];      // Unique schema list
        var tables = [];       // Unique table list
        var storedProcedures = [];
        var scalarFunctions = [];
        var tableFunctions = [];
        var objectTypes = {};
        var columnDetails = {};
        var routineParameters = {};
        var databases = [];
        var activeDatabase = '';
        var databaseMetadata = {};
        var pendingDatabaseMetadata = {};
        var metadataLoaded = false;
