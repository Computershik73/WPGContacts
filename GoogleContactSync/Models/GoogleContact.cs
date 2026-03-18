// Models/GoogleContact.cs
// Data models for Google People API responses.

using System.Collections.Generic;

namespace GoogleContactSync.Models
{
    public class GoogleContact
    {
        public string Id       { get; set; } = "";
        public string ETag     { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName  { get; set; } = "";
        public string Nickname  { get; set; } = "";
        public string Notes     { get; set; } = "";
        public string PhotoUrl    { get; set; } = "";
    public string UpdateTime  { get; set; } = ""; // from metadata.sources[].updateTime

        public List<GPhone>   Phones        { get; set; } = new List<GPhone>();
        public List<GEmail>   Emails        { get; set; } = new List<GEmail>();
        public List<GAddress> Addresses     { get; set; } = new List<GAddress>();
        public List<GUrl>     Urls          { get; set; } = new List<GUrl>();
        public List<GOrg>     Organizations { get; set; } = new List<GOrg>();

        public GDate Birthday { get; set; } = null;
    }

    public class GPhone   { public string Number { get; set; } = ""; public string Type { get; set; } = "other"; }
    public class GEmail   { public string Address { get; set; } = ""; public string Type { get; set; } = "other"; }
    public class GUrl     { public string Value { get; set; } = ""; public string Type { get; set; } = "other"; }
    public class GOrg     { public string Name { get; set; } = ""; public string Title { get; set; } = ""; }
    public class GAddress
    {
        public string Street     { get; set; } = "";
        public string City       { get; set; } = "";
        public string Region     { get; set; } = "";
        public string PostalCode { get; set; } = "";
        public string Country    { get; set; } = "";
        public string Type       { get; set; } = "home";
    }
    public class GDate { public int? Year { get; set; } public uint Month { get; set; } public uint Day { get; set; } }
}
