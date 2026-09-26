using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DP.LabelInspection.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DP.LabelInspection.Storage;

public sealed partial class InspectionStore
{
    private sealed class RecipeResolver : CamelCasePropertyNamesContractResolver
    {
        protected override JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            MemberSerialization serialization
        )
        {
            var property = base.CreateProperty(member, serialization);
            if (
                member.DeclaringType == typeof(InspectionRegion)
                    && member.Name == nameof(InspectionRegion.Tasks)
                || member.DeclaringType == typeof(RegionInspectionResult)
                    && (
                        member.Name == nameof(RegionInspectionResult.Execution)
                        || member.Name == nameof(RegionInspectionResult.Anomaly)
                    )
                || member.DeclaringType == typeof(InspectionFinding)
                    && member.Name == nameof(InspectionFinding.IsExecutionBlocker)
            )
            {
                property.Writable = true;
            }

            if (
                member.DeclaringType == typeof(InspectionRegion)
                && member.Name == nameof(InspectionRegion.Field)
            )
            {
                property.ShouldSerialize = value =>
                    ((InspectionRegion)value).Kind == ERegionKind.Text
                    || ((InspectionRegion)value).Kind == ERegionKind.Barcode;
            }

            return property;
        }
    }
}
