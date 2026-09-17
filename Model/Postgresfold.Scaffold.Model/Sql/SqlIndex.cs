namespace Postgresfold.Scaffold.Model.Sql
{
    public class SqlIndex
    {
        public string IndexName { get; set; }
        public bool IsUnique { get; set; }
        public string IndexType { get; set; }
        public string Column { get; set; }
    }
}
