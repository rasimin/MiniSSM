namespace SSMS
{
    public class ObjectExplorerNode
    {
        public string NodeType { get; set; } = string.Empty; // "Server", "DatabasesFolder", "Database", "TablesFolder", "Table", "ColumnsFolder", "Column"
        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string DetailName { get; set; } = string.Empty;
    }
}
