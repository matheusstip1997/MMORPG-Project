using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Data
{
    /// <summary>Every config table the game loads. Add a row type here when you add a table file.</summary>
    public static class ConfigTables
    {
        public static readonly IReadOnlyList<Type> All = new[]
        {
            typeof(StartingValuesM1),
            typeof(NetSecurity),
            typeof(EventBusLimits),
            typeof(TelemetrySettings),
            typeof(ServerLoop),
        };
    }
}
