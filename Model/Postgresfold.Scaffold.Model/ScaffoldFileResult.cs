using Postgresfold.Scaffold.Model.Enum;

namespace Postgresfold.Scaffold.Model
{
    public class ScaffoldFileResult
    {
        public ScaffoldResult ScaffoldResult { get; set; }
        public string FilePath { get; set; }

        public ScaffoldFileResult(ScaffoldResult result, string path)
        {
            ScaffoldResult = result;
            FilePath = path;
        }
    }
}
