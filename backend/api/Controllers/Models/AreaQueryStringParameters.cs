﻿namespace Api.Controllers.Models
{
    public class AreaQueryStringParameters : QueryStringParameters
    {
        public AreaQueryStringParameters()
        {
            // Default order is installation code
            OrderBy = "InstallationCode installationCode";
        }

        /// <summary>
        /// Filter for the installation code of the mission
        /// </summary>
        public string? InstallationCode { get; set; }

        /// <summary>
        /// Filter for the deck of the mission
        /// </summary>
        public string? Deck { get; set; }
    }
}
