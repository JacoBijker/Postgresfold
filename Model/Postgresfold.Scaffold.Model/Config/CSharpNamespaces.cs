namespace Postgresfold.Scaffold.Model.Config
{
    public class CSharpNamespaces
    {
        public string RootNamespace { get; set; }
        public string ModelNamespace { get; set; }
        public string CommonNamespace { get; set; }
        public string CommonUtilNamespace { get; set; }
        public string DomainNamespace { get; set; }
        public string DalNamespace { get; set; }
        public string DomainInterfaceNamespace { get; set; }
        public string DalInterfaceNamespace { get; set; }
        public string ServiceCollectionExtensionNamespace { get; set; }


        public CSharpNamespaces(string rootNamespace)
        {
            RootNamespace = rootNamespace;
            ModelNamespace = $"{rootNamespace}.Model.#SCHEMA#";
            CommonNamespace = $"{rootNamespace}.Common";
            CommonUtilNamespace = $"{rootNamespace}.Common.Util";
            DomainNamespace = $"{rootNamespace}.Domain.#SCHEMA#";
            DalNamespace = $"{rootNamespace}.Dal.Dapper.#SCHEMA#";
            DomainInterfaceNamespace = $"{rootNamespace}.Domain.Interface.#SCHEMA#";
            DalInterfaceNamespace = $"{rootNamespace}.Dal.Interface.#SCHEMA#";
            ServiceCollectionExtensionNamespace = $"{rootNamespace}.ServiceCollectionExtension.#SCHEMA#.Gen";
        }
    }
}
