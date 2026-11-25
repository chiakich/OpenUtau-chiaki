using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.Agent {
    static class Queries {
        public static JsonObject Singer(USinger singer) {
            return new JsonObject {
                ["id"] = singer.Id,
                ["name"] = singer.Name,
                ["type"] = singer.SingerType.ToString().ToLowerInvariant(),
                ["path"] = singer.Location,
                ["version"] = NullIfEmpty(singer.Version),
                ["author"] = NullIfEmpty(singer.Author),
                ["default_phonemizer"] = NullIfEmpty(singer.DefaultPhonemizer),
                ["errors"] = new JsonArray((singer.Errors ?? Array.Empty<string>()).Select(e => (JsonNode)e).ToArray()),
            };
        }

        public static JsonArray Singers() {
            return new JsonArray(SingerManager.Inst.Singers.Values
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (JsonNode)Singer(s))
                .ToArray());
        }

        // A voicebank folder, or an installed singer by id or name.
        public static USinger ResolveSinger(IAgentHost host, string value) {
            if (Directory.Exists(value) && File.Exists(Path.Combine(value, OpenUtau.Classic.VoicebankLoader.kCharTxt))) {
                return host.AddSingerDirectory(value);
            }
            host.SearchInstalledSingers();
            var singer = SingerManager.Inst.GetSinger(value)
                ?? SingerManager.Inst.Singers.Values.FirstOrDefault(s => s.Name == value);
            if (singer == null) {
                throw new AgentException("singer_not_found",
                    $"Singer \"{value}\" is neither a voicebank folder nor an installed singer. List them with the singers command.");
            }
            return singer;
        }

        // The expressions a track with this singer offers in the app, with the ranges the renderer honours.
        public static JsonObject Expressions(IAgentHost host, USinger singer) {
            return host.Invoke(() => {
                var project = Core.Format.Ustx.Create();
                var track = project.tracks[0];
                track.Singer = singer;
                track.RendererSettings = new URenderSettings {
                    renderer = Renderers.GetDefaultRenderer(singer.SingerType),
                };
                project.Validate(new ValidateOptions { SkipPhonemizer = true });
                if (!singer.Loaded) {
                    throw new AgentException("singer_not_loaded",
                        $"Singer \"{singer.Id}\" failed to load: {string.Join("; ", singer.Errors ?? Array.Empty<string>())}");
                }
                ExpressionSuggestionSync.UpsertSuggested(project, track);
                project.Validate(new ValidateOptions { SkipPhonemizer = true });
                var expressions = new JsonArray();
                foreach (var descriptor in track.GetSupportedExps(project).OrderBy(d => d.abbr, StringComparer.Ordinal)) {
                    track.TryGetExpDescriptor(project, descriptor.abbr, out var effective);
                    effective ??= descriptor;
                    expressions.Add(new JsonObject {
                        ["abbr"] = effective.abbr,
                        ["name"] = effective.name,
                        ["type"] = TypeName(effective.type),
                        ["min"] = effective.min,
                        ["max"] = effective.max,
                        ["default"] = effective.CustomDefaultValue,
                        ["options"] = effective.type == UExpressionType.Options && effective.options != null
                            ? new JsonArray(effective.options.Select(o => (JsonNode)o).ToArray())
                            : null,
                    });
                }
                var info = Singer(singer);
                info["renderer"] = track.RendererSettings.renderer;
                if (singer is DiffSingerSinger ds) {
                    info["diffsinger"] = DiffSinger(ds);
                }
                return new JsonObject {
                    ["singer"] = info,
                    ["expressions"] = expressions,
                };
            });
        }

        static JsonObject DiffSinger(DiffSingerSinger singer) {
            var config = singer.dsConfig;
            return new JsonObject {
                ["sample_rate"] = config.sample_rate,
                ["hop_size"] = config.hop_size,
                ["frame_ms"] = ProjectReport.Round(config.frameMs()),
                ["phonemes"] = singer.phonemes.Count,
                ["languages"] = new JsonArray(singer.languageIds
                    .OrderBy(pair => pair.Value)
                    .Select(pair => (JsonNode)pair.Key)
                    .ToArray()),
                ["voice_colors"] = new JsonArray(singer.Subbanks
                    .Select(subbank => (JsonNode)new JsonObject { ["color"] = subbank.Color, ["suffix"] = subbank.Suffix })
                    .ToArray()),
                ["has_pitch_predictor"] = singer.HasPitchPredictor,
                ["has_variance_predictor"] = singer.HasVariancePredictor,
                ["use_variable_depth"] = config.useVariableDepth,
                ["max_depth"] = config.useVariableDepth ? ProjectReport.Round(config.maxDepth) : null,
                ["use_continuous_acceleration"] = config.useContinuousAcceleration,
                ["use_lang_id"] = config.use_lang_id,
                ["use_key_shift_embed"] = config.useKeyShiftEmbed,
                ["use_speed_embed"] = config.useSpeedEmbed,
                ["use_energy_embed"] = config.useEnergyEmbed,
                ["use_breathiness_embed"] = config.useBreathinessEmbed,
                ["use_voicing_embed"] = config.useVoicingEmbed,
                ["use_tension_embed"] = config.useTensionEmbed,
                ["use_mouth_opening_embed"] = config.useMouthOpeningEmbed,
            };
        }

        public static JsonArray Phonemizers(USinger? singer) {
            var factories = singer != null
                ? PhonemizerFactory.EnumerateForSinger(singer)
                : PhonemizerFactory.GetAll();
            return new JsonArray(factories
                .OrderBy(f => f.tag, StringComparer.Ordinal)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .Select(f => (JsonNode)new JsonObject {
                    ["id"] = f.Id,
                    ["name"] = f.name,
                    ["tag"] = f.tag,
                    ["language"] = NullIfEmpty(f.language),
                    ["author"] = NullIfEmpty(f.author),
                })
                .ToArray());
        }

        static string TypeName(UExpressionType type) {
            switch (type) {
                case UExpressionType.Numerical: return "numerical";
                case UExpressionType.Options: return "options";
                case UExpressionType.Curve: return "curve";
                default: return "masked_curve";
            }
        }

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
