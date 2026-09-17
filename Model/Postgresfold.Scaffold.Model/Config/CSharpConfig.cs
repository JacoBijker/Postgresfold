namespace Postgresfold.Scaffold.Model.Config
{
    public class CSharpConfig
    {
        public string DbUpProjectFile { get; set; }
        public CSharpNamespaces Namespaces { get; set; }
        public CSharpDirectories Directories { get; set; }

        public CSharpConfig()
        { }

        public CSharpConfig(string solutionDirectory, string rootNamespace, string dbUpProjectFile)
        {
            DbUpProjectFile = dbUpProjectFile;
            Namespaces = new CSharpNamespaces(rootNamespace);
            Directories = new CSharpDirectories(solutionDirectory, rootNamespace);
        }
    }
}
