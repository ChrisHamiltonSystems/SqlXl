namespace SqlXl.Models;
public class MenuItem
{
    public int ID { get; set; }                    // Primary key for the menu item
    public string DisplayName { get; set; }       // The name to display in the menu
    public string ControllerName { get; set; }    // The MVC controller associated with this item
    public string ActionName { get; set; }        // The action method associated with this item
    public string QueryString { get; set; }       // Optional query string parameters
    public int SortOrder { get; set; }            // Determines the order of menu items
}//end class