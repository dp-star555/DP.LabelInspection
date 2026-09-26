using System;
using System.Linq;
using DP.LabelInspection.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.LabelInspection.Tests;

/// <summary>枚举E前缀重命名后，保留配方、报告和能力标志的成员及数值。</summary>
[TestClass]
public sealed class EnumContractTests
{
    /// <summary>类型名变化不能改变已存配方或报告采用的枚举数值。</summary>
    /// <param name="type">需要检查的标签契约枚举。</param>
    /// <param name="expected">重构前的成员名称及整数值。</param>
    [TestMethod]
    [DataRow(typeof(EBarcodeKind), "Auto=0,OneDimensional=1,QrCode=2")]
    [DataRow(typeof(EBindingSource), "Region=0,TaskData=1")]
    [DataRow(typeof(EImagePixelFormat), "Gray8=0,Bgr24=1")]
    [DataRow(typeof(ERegionKind), "Fixed=0,Text=1,Barcode=2,Blank=3,Ignore=4")]
    [DataRow(typeof(EInspectionMode), "Free=0,Template=1")]
    [DataRow(typeof(EAlignmentMode), "AssumeAligned=0,Translation=1")]
    [DataRow(typeof(EInspectionVerdict), "Ok=0,Review=1,Ng=2")]
    [DataRow(
        typeof(EInspectionCapabilities),
        "None=0,Quality=1,FixedDifference=2,BlankSpots=4,TranslationAlignment=8,Ocr=16,CharacterSegmentation=32,GlyphComparison=64,BarcodeDecode=128,BarcodeStructure=256,Discovery=512"
    )]
    [DataRow(typeof(ERoiStageState), "NotRequested=0,NotExecuted=1,Passed=2,Failed=3")]
    [DataRow(typeof(EAnomalyModelScope), "Region=0,Character=1")]
    public void NamesAndValuesRemainExplicit(Type type, string expected)
    {
        Assert.IsTrue(type.IsEnum && type.Name.StartsWith("E", StringComparison.Ordinal));
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(type));
        string actual = string.Join(
            ",",
            Enum.GetNames(type).Select(name => name + "=" + Convert.ToInt32(Enum.Parse(type, name)))
        );
        Assert.AreEqual(expected, actual);
    }

    /// <summary>能力枚举仍可组合位标志，不能因改名丢失Flags属性。</summary>
    [TestMethod]
    public void CapabilitiesRemainFlags()
    {
        Assert.IsTrue(Attribute.IsDefined(typeof(EInspectionCapabilities), typeof(FlagsAttribute)));
        Assert.AreEqual(
            130,
            (int)(EInspectionCapabilities.FixedDifference | EInspectionCapabilities.BarcodeDecode)
        );
    }
}
