namespace Postgresfold.Scaffold.Model.Sql
{
    public class SqlTable
    {
        public string TableName { get; set; }
        public string Schema { get; set; }
        public List<SqlColumn> Columns { get; set; } = new();
        public List<SqlConstraint> Constraints { get; set; } = new();
        public List<SqlIndex> Indexes { get; set; } = new();
        public List<SqlFullTextIndex> FullTextIndexes { get; set; } = new();
    }
}
