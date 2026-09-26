using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

internal static partial class RegionEditor
{
    private sealed class EditableRegion
    {
        /// <summary>复制ROI配置为属性面板编辑状态，不修改原始快照。</summary>
        /// <param name = "r">要复制为属性面板可编辑状态的不可变ROI配置。</param>
        public EditableRegion(InspectionRegion r)
        {
            ReadData = r.Tasks.ReadData;
            CheckQuality = r.Tasks.CheckQuality;
            DetectAnomaly = r.Tasks.DetectAnomaly;
            AnomalyLibraryId = r.Anomaly?.LibraryId;
            AnomalyLibraryRevision = r.Anomaly?.LibraryRevision;
            AnomalyModelKey = r.Anomaly?.ModelKey;
            Name = r.Name;
            Kind = r.Kind;
            X = r.Bounds.X;
            Y = r.Bounds.Y;
            Width = r.Bounds.Width;
            Height = r.Bounds.Height;
            SingleLine = r.SingleLine;
            var f = r.Field;
            LibraryId = f.LibraryId;
            LibraryRevision = f.LibraryRevision;
            Expected = f.Expected;
            Pattern = f.Pattern;
            AllowedCharacters = f.AllowedCharacters;
            MinimumLength = f.MinimumLength;
            MaximumLength = f.MaximumLength;
            EqualCells = f.EqualCells;
            MaximumDifference = f.MaximumDifference;
            GlyphTolerance = f.GlyphTolerance;
            MinimumConfidence = f.MinimumConfidence;
            BarcodeType = f.BarcodeType;
            CheckQrQuietZone = f.BarcodePrint.CheckQrQuietZone;
            DetectInkLoss = f.BarcodePrint.DetectInkLoss;
            MinimumInkLoss = f.BarcodePrint.MinimumInkLoss;
            BarcodeMinimumArea = f.BarcodePrint.MinimumArea;
            BarcodeMinimumFraction = f.BarcodePrint.MinimumFraction;
            BarcodeEdgeTolerance = f.BarcodePrint.EdgeTolerance;
        }

        [
            Category("00 检测项目"),
            DisplayName("读取实际数据"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "文字执行OCR，条码/QR执行读码。固定、空白不支持数据项目。配置业务引导值时必须开启；文字质量策略需要OCR身份时，即使不选业务数据也仍须具备OCR能力。"
            )
        ]
        public bool ReadData { get; set; }

        [
            Category("00 检测项目"),
            DisplayName("A 规则质检（按类型）"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "质量方法A：文字按单字库逐字比较，条码按条/空隙或QR模块检查，固定内容与整图参考比较，空白区查污点。选择则必须具备对应类型的质检能力及参考；缺条件直接本ROI NG。未选择不执行质量专用分割/配对/比较。读数正确不代表质量合格；当前ROI失败不影响后续ROI继续。与B可单独或同时启用，同时启用时任一NG即NG。"
            )
        ]
        public bool CheckQuality { get; set; }

        [
            Category("00 检测项目"),
            DisplayName("B 局部异常检测（良品模型）"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "质量方法B：只用良品训练的局部块异常检测，适用于文字、条码、固定内容和空白区，不需要OCR或字库。需在“07 异常检测”绑定异常模型库的固定版本，库中须有该ROI的模型（默认按ROI名称）；缺模型、版本不可用或ROI尺寸已变（位置相关模型）时本ROI NG。与A可单独或同时启用，同时启用时任一NG即NG；A应付不了的情况可用B兜底。"
            )
        ]
        public bool DetectAnomaly { get; set; }

        [
            Category("07 异常检测（方法B）"),
            DisplayName("异常模型库ID"),
            Description(
                "异常模型库标识，不是显示名称。建议通过下方模型库下拉框和“绑定所选异常模型库/版本”设置。ID与版本必须同时指定；清除绑定时两者都清空。"
            )
        ]
        public string? AnomalyLibraryId { get; set; }

        [
            Category("07 异常检测（方法B）"),
            DisplayName("异常模型库版本（修订号）"),
            Description(
                "大于等于1的整数，固定使用该修订。重新训练或替换模型会发布新修订，不会自动升级此ROI；需重新绑定。"
            )
        ]
        public int? AnomalyLibraryRevision { get; set; }

        [
            Category("07 异常检测（方法B）"),
            DisplayName("模型键"),
            Description(
                "库内模型的键，留空表示使用本ROI名称（训练时默认按ROI名称保存）。多个ROI内容完全相同时可指向同一模型；位置相关模型要求裁图尺寸与训练一致。"
            )
        ]
        public string? AnomalyModelKey { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("区域名称"),
            Description(
                "本ROI的名称，不能为空、最长100字符，同一配方内必须唯一。用于报告与字段绑定识别；重命名后请检查相关绑定。"
            )
        ]
        public string Name { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("检查类型"),
            TypeConverter(typeof(ChineseRegionKindConverter)),
            Description(
                "文字：OCR及已绑定字库比较；条码：读取及可选印刷检查；固定内容：整图模板差异；空白区：污点；忽略区：排除范围。下面标明“仅文字/仅条码”的参数不会作用于其他类型。"
            )
        ]
        public ERegionKind Kind { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("左上角X（原图像素）"),
            Description(
                "ROI左上角相对原图左边的距离，单位为原始像素，整数且不小于0。不是缩放后屏幕坐标；必须保证整个框在待检图内。"
            )
        ]
        public int X { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("左上角Y（原图像素）"),
            Description(
                "ROI左上角相对原图上边的距离，单位为原始像素，整数且不小于0。增大向下移动；必须保证整个框在待检图内。"
            )
        ]
        public int Y { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("区域宽度（原图像素）"),
            Description(
                "ROI水平宽度，正整数，单位为原始像素。文字框须包含完整左右笔画，避免截字或带入相邻条码；框不能超出原图。"
            )
        ]
        public int Width { get; set; }

        [
            Category("01 区域位置与类型"),
            DisplayName("区域高度（原图像素）"),
            Description(
                "ROI垂直高度，正整数，单位为原始像素。文字需保留完整上下笔画及少量白边；过大可能带入条码，过小会触发裁切/分割失败。自动单行分割高度不超过512。"
            )
        ]
        public int Height { get; set; }

        [
            Category("02 文字分割（仅文字）"),
            DisplayName("已框选横向单行"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "是：声明该文字ROI仅包含一条横向文字，允许执行单行OCR与物理分割。否：未声明单行，当前正式路径不会自行做多行布局分析；已要求字库比较却无法完成时判NG。"
            )
        ]
        public bool SingleLine { get; set; }

        [
            Category("03 单字外观（仅文字）"),
            DisplayName("字库类别ID"),
            Description(
                "要比较的单字参考库标识，不是字库显示名称。建议通过下方字库下拉框和“绑定所选类别/版本”设置，避免手填错误。类别ID与版本必须同时指定；未绑定的普通OCR不执行字库比较。"
            )
        ]
        public string? LibraryId { get; set; }

        [
            Category("03 单字外观（仅文字）"),
            DisplayName("字库版本（修订号）"),
            Description(
                "大于等于1的整数，固定使用该修订。字库补字后不会自动升级此ROI；需在下方重新绑定新修订。清除绑定时类别ID和版本均须清空。缺参考字或版本不可用导致必检比对未完成时判NG。"
            )
        ]
        public int? LibraryRevision { get; set; }

        [
            Category("04 内容约束（文字/条码）"),
            DisplayName("预期完整内容"),
            Description(
                "最长1024字符，与识别/解码文本做区分大小写的完整比较，不自动纠正O/0。留空表示不按此项固定整段内容。启用等分切字时，必须填写单元从左到右的ASCII字母/数字，数量决定单元数；不要给随机可变序列随意填写固定值。"
            )
        ]
        public string? Expected { get; set; }

        [
            Category("04 内容约束（文字/条码）"),
            DisplayName("内容格式（正则表达式）"),
            Description(
                "可选的整段匹配规则，表达式最长512字符；留空禁用。例如 [0-9]{8} 表示恰好8位数字，[A-Z][0-9]{6} 表示1位大写字母接6位数字。这是内容检查，不是字形比较；不熟悉正则时可留空。"
            )
        ]
        public string? Pattern { get; set; }

        [
            Category("04 内容约束（文字/条码）"),
            DisplayName("允许出现的字符"),
            Description(
                "可选字符白名单，最长1024字符，直接列出允许的字符，例如0123456789。不是正则表达式，不表示字符顺序；留空禁用这一约束。区分大小写。"
            )
        ]
        public string? AllowedCharacters { get; set; }

        [
            Category("05 码制声明（仅条码）"),
            DisplayName("条码类型"),
            TypeConverter(typeof(ChineseBarcodeKindConverter)),
            Description(
                "自动：由解码及结构线索确定；一维条码：按条/空隙分析；二维码（QR）：按QR模块分析。已声明QR时，读码或结构失败不会偷偷退回一维检查。"
            )
        ]
        public EBarcodeKind BarcodeType { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("检查QR四周留白"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "仅QR且印刷检查开启时生效。是：检查四周4模块静区，ROI必须包含完整留白；否：不检查静区，默认否。静区污点按最小面积判断，不按整码面积比例稀释。"
            )
        ]
        public bool CheckQrQuietZone { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("检测一维条码墨色变浅"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "仅一维条码且印刷检查开启时生效，默认是。除二值化后的孔洞外，补充检查条内部灰度变浅；去掉两侧条边容差后内部不足4像素的窄条，灰度起伏由成像模糊主导，只按二值化缺墨判定。不作用于QR或单字库，不替代缺墨/多墨检查。"
            )
        ]
        public bool DetectInkLoss { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("最小墨色损失比例"),
            Description(
                "仅一维墨色变浅检查。范围大于0且不超过1，默认0.25（25%）；以本列深色参考和ROI可用对比度计算，还要求至少相差20灰度级。调小更敏感，调大更宽松；不是单字差异阈值。"
            )
        ]
        public double MinimumInkLoss { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("最小异常面积（原图像素²）"),
            Description(
                "范围1–1000000，默认4，单位为原始像素面积。一维按连通候选，QR按单模块内部聚合差异判断。调小更易检出小点也更易误报；调大忽略更多小点。通常还需达到异常面积比例；QR静区单独按面积判断。"
            )
        ]
        public int BarcodeMinimumArea { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("最小异常面积比例"),
            Description(
                "范围0–1，默认0.01（1%）。一维码分母是当前条/空隙面积，QR分母是一个模块面积，不是整幅ROI。调小更敏感，调大更宽松；通常需同时达到最小异常面积。不是单字字形差异比例。"
            )
        ]
        public double BarcodeMinimumFraction { get; set; }

        [
            Category("06 印刷质量（仅条码）"),
            DisplayName("条码边缘排除（原图像素）"),
            Description(
                "范围0–8，默认1。条/空隙或QR模块交界处的边缘带宽度（原图像素）：只触及边缘带的毛刺、渗墨视为印刷波动；一维码条的上下端另有条端区（条高10%），从条端开始且不超过条端区的缩短/渐淡视为条长波动，不计入；深入内部的缺墨/多墨按完整面积计入，不再被缩小。QR整体墨迹扩散时自动加宽对应方向，上限为模块半宽的40%。半宽不超过边缘带的细条/细空隙只检查横贯整条宽度的断裂。调大更宽松；不是单字的归一化边缘容差。"
            )
        ]
        public int BarcodeEdgeTolerance { get; set; }

        [
            Category("04 内容约束（文字/条码）"),
            DisplayName("最少字符数"),
            Description(
                "内容长度下限，整数且不小于0，默认0；不得大于最多字符数。调大表示不接受更短文本；不是要强制切出的字块数。"
            )
        ]
        public int MinimumLength { get; set; }

        [
            Category("04 内容约束（文字/条码）"),
            DisplayName("最多字符数"),
            Description(
                "内容长度上限，默认128，不得小于最少字符数，最大1024。限制读取内容长度，不会强行把图像等分到此数量；自动单行分割仍最多128个字符。"
            )
        ]
        public int MaximumLength { get; set; }

        [
            Category("02 文字分割（仅文字）"),
            DisplayName("按等宽单元切字"),
            TypeConverter(typeof(ChineseBooleanConverter)),
            Description(
                "默认否，使用OCR与物理分割。是：由你明确保证ROI内字符等宽、对齐，并用“预期完整内容”的字数等分；不依赖OCR确定身份。只适用于确实等分排版，不可为解决粘连而随意开启，否则会切伤字符。需绑定字库。"
            )
        ]
        public bool EqualCells { get; set; }

        [
            Category("03 单字外观（仅文字）"),
            DisplayName("最大允许字形差异比例"),
            Description(
                "范围0–5，默认0.05（5%）。实拍良品单字差异最大约0.025，明显缺墨的字约0.06起；旧配方保存的值（如0.18）不会自动改变。差异=(计入的缺墨像素+多墨像素)/归一化参考墨迹面积；只触及笔画边缘带的印刷波动不计入，良品通常为0或接近0，因此可用一批良品实测后在其最大值之上留余量设较小阈值以检出局部缺墨。超过此值产生超差记录，并非OCR置信度。调小更严格，调大更宽松；局部损伤仍按整字面积折算比例。"
            )
        ]
        public double MaximumDifference { get; set; }

        [
            Category("03 单字外观（仅文字）"),
            DisplayName("单字边缘容差（归一化像素）"),
            Description(
                "范围0–8，默认2。单位是归一化字图像素，不是原图像素。差异按块判断：只触及笔画边缘带（本值，整体偏粗/偏细时自动加宽）的缺墨/多墨视为印刷波动，不计入；深入笔画（达到局部笔画半宽的一半）、贯穿笔画或整段缺失的差异按完整面积计入，不会因容差被缩小。调大更宽松、可能漏掉浅的边缘缺口；调小更敏感。设0时差异仍须深入笔画才计入，也不关闭裁边缩放及自动对齐。"
            )
        ]
        public int GlyphTolerance { get; set; }

        [
            Category("02 文字分割（仅文字）"),
            DisplayName("最低OCR置信度"),
            Description(
                "范围0–1，默认0.75（75%）。低于此值，字符身份不可靠，普通OCR路径会跳过字形比较；已要求比对但未完成时判NG。调高更严格，调低允许更多不确定身份；高置信度不是识别正确率或字形合格证明。"
            )
        ]
        public double MinimumConfidence { get; set; }

        internal InspectionRegion Build()
        {
            return new InspectionRegion(
                Name,
                Kind,
                new PixelRect(X, Y, Width, Height),
                Kind == ERegionKind.Text && SingleLine,
                Kind == ERegionKind.Text || Kind == ERegionKind.Barcode
                    ? new FieldSettings(
                        string.IsNullOrWhiteSpace(LibraryId) ? null : LibraryId,
                        LibraryRevision,
                        Empty(Expected),
                        Empty(Pattern),
                        Empty(AllowedCharacters),
                        MinimumLength,
                        MaximumLength,
                        EqualCells,
                        MaximumDifference,
                        GlyphTolerance,
                        MinimumConfidence,
                        new BarcodePrintOptions(
                            CheckQuality,
                            BarcodeMinimumArea,
                            BarcodeMinimumFraction,
                            BarcodeEdgeTolerance,
                            CheckQrQuietZone,
                            DetectInkLoss,
                            MinimumInkLoss
                        ),
                        BarcodeType
                    )
                    : null,
                Anomaly()
            ).WithTasks(new RoiInspectionTasks(ReadData, CheckQuality, DetectAnomaly));
        }

        private AnomalySettings? Anomaly()
        {
            if (Kind == ERegionKind.Ignore || string.IsNullOrWhiteSpace(AnomalyLibraryId))
            {
                return null;
            }

            if (AnomalyLibraryRevision == null)
            {
                throw new ArgumentException(Name + "：异常模型库ID与版本必须同时指定。");
            }

            return new AnomalySettings(
                AnomalyLibraryId!.Trim(),
                AnomalyLibraryRevision.Value,
                string.IsNullOrWhiteSpace(AnomalyModelKey) ? null : AnomalyModelKey
            );
        }

        private static string? Empty(string? value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public override string ToString()
        {
            return Name + " / " + new ChineseRegionKindConverter().ConvertToString(Kind);
        }
    }
}
