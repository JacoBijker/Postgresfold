namespace Postgresfold.Scaffold.Model.Sql
{
    public class SqlStoredProcedure
    {
        public string TableName { get; set; }
        public string Schema { get; set; }
        public string StoredProcedureName { get; set; }
        public List<SqlColumn> Parameters { get; set; } = new();
        public string? CustomReturnType { get; set; }
    }
}
