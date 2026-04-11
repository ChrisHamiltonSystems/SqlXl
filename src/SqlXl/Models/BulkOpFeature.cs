namespace SqlXl.Models;
public class BulkOpFeature
{   /* Corresponds to table: ZZSqlXL.BulkOpFeatures */
    public int ID { get; set; }

    public string UserFriendlyFeatureName { get; set; }

    public string InsertUpdateDeleteOrCustom { get; set; }

    public string DomainSchemaName { get; set; }

    public string DomainTableName { get; set; }

    public string StagingSchemaName { get; set; }

    public string StagingTableName { get; set; }

    public string GetRowsToChooseFrom_SelectStatement { get; set; }

    public string GetRowsToEdit_SelectStatement { get; set; }

    public string SprocToProcessPerfectStagedData { get; set; }

    public int MenuDisplayRanking { get; set; }

    // Note: RequiresCtxVar001-012 flags may exist in DB but omitted from model for now <<ToDo what is this?
}//end class