namespace Postgresfold.Scaffold.Model.Enum
{
    public enum ScaffoldResult : int
    {
        /// <summary>
        /// Created a brand new file
        /// </summary>
        Created = 1,

        /// <summary>
        /// Updated an existing file
        /// </summary>
        Updated = 2,

        /// <summary>
        /// Did not make any updated
        /// </summary>
        Skipped = 3,

        /// <summary>
        /// Deleted the file
        /// </summary>
        Deleted = 4
    }
}
