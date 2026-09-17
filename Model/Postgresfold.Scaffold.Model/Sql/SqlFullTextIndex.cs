namespace Postgresfold.Scaffold.Model.Sql
{
    public class SqlFullTextIndex
    {
        public string IndexName { get; set; }
        public string IndexType { get; set; }
        public List<string> Columns { get; set; }
    }
}
