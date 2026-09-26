using System.Collections.Generic;
using System.Linq;
using DP.LabelInspection.Contracts;

namespace DP.LabelInspection;

public sealed partial class CharacterAnomalyBuilderControl
{
    /// <summary>一张图上一个文字ROI的提取结果：分割出的字符、可人工修改的身份及是否作为训练样本。</summary>
    private sealed class LineCandidate
    {
        internal LineCandidate(
            ImageFrame image,
            string source,
            string region,
            CharacterSegmentation segmentation
        )
        {
            Image = image;
            Source = source;
            Region = region;
            Segmentation = segmentation;
            Characters = segmentation.Characters.ToList();
            Labels = Characters.Select(c => c.Character).ToArray();
            // 需复核的切割（如切过粘连）默认不作为样本，由人工逐个确认后勾选。
            Include = Characters.Select(_ => segmentation.Status == "provisional").ToArray();
        }

        internal ImageFrame Image { get; }

        internal string Source { get; }

        internal string Region { get; }

        internal CharacterSegmentation Segmentation { get; }

        internal List<CharacterPatch> Characters { get; }

        internal string[] Labels { get; }

        internal bool[] Include { get; }

        /// <summary>按人工确认的身份生成训练样本；未勾选的字符只参与行几何测量。</summary>
        internal CharacterAnomalySample ToSample()
        {
            var relabelled = Characters.Select(
                (c, i) =>
                    Labels[i] == c.Character
                        ? c
                        : new CharacterPatch(Labels[i], c.TokenIndex, c.Bounds, c.Patch, c.NeighborInkRemoved)
            );
            return new CharacterAnomalySample(
                Image,
                relabelled,
                Enumerable.Range(0, Characters.Count).Where(i => !Include[i]),
                Source + " / " + Region
            );
        }
    }
}
