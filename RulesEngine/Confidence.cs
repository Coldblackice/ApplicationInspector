﻿using System.Text.Json.Serialization;

namespace Microsoft.ApplicationInspector.RulesEngine
{
    using System;

    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Confidence { Unspecified = 0, Low = 1, Medium = 2, High = 4 }
}