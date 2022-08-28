/*
 * Code and classes for config file options the program uses.
 */

using System;
using System.Collections.Generic;
using CommandLine;

namespace GoldenSyrupGames.T2MD
{
    // options stored in t2md.json
    class ConfigFileOptions
    {
        public string ApiKey { get; set; } = "";
        public string ApiToken { get; set; } = "";
    }
}
