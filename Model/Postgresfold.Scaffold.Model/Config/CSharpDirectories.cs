namespace Postgresfold.Scaffold.Model.Config
{
    public class CSharpDirectories
    {
        public string SolutionDirectory { get; set; }
        public string ModelDirectory { get; set; }
        public string CommonDirectory { get; set; }
        public string CommonServiceCollectionExtensionDirectory { get; set; }
        public string DomainDirectory { get; set; }
        public string DalDirectory { get; set; }
        public string DomainInterfaceDirectory { get; set; }
        public string DalInterfaceDirectory { get; set; }
        public string ServiceCollectionExtensionDalDirectory { get; set; }
        public string ServiceCollectionExtensionDomainDirectory { get; set; }

        public CSharpDirectories(string solutionDirectory, string rootNamespace)
        {
            SolutionDirectory = solutionDirectory;
            CommonDirectory = Path.Combine(solutionDirectory, "Common", $"{rootNamespace}.Common");
            ModelDirectory = Path.Combine(solutionDirectory, "Model", $"{rootNamespace}.Model", "#SCHEMA#");

            DomainDirectory = Path.Combine(solutionDirectory, "Domain", $"{rootNamespace}.Domain", "#SCHEMA#", "Gen");
            DomainInterfaceDirectory = Path.Combine(solutionDirectory, "Domain", $"{rootNamespace}.Domain.Interface", "#SCHEMA#", "Gen");
            DalDirectory = Path.Combine(solutionDirectory, "Dal", $"{rootNamespace}.Dal.Dapper", "#SCHEMA#", "Gen");
            DalInterfaceDirectory = Path.Combine(solutionDirectory, "Dal", $"{rootNamespace}.Dal.Interface", "#SCHEMA#", "Gen");

            CommonServiceCollectionExtensionDirectory = Path.Combine(solutionDirectory, "Common", $"{rootNamespace}.Common.ServiceCollectionExtension");
            ServiceCollectionExtensionDalDirectory = Path.Combine(CommonServiceCollectionExtensionDirectory, "#SCHEMA#", "Gen");
            ServiceCollectionExtensionDomainDirectory = Path.Combine(CommonServiceCollectionExtensionDirectory, "#SCHEMA#", "Gen");
        }
    }
}
