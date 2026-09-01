using System.IO;
using System.Text;
using System.Xml;
using OpenCortex.CortexUSB.Models;
using CortexProtobufV2;

namespace OpenCortex.CortexUSB.Protocol
{
    public static class ModelCatalog
    {
        private static readonly Dictionary<string, ParamType> TypeStringMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["float"] = ParamType.Float,
            ["int"] = ParamType.Int,
            ["switch"] = ParamType.Switch,
            ["rotarySwitch"] = ParamType.RotarySwitch,
            ["rotaryswitch"] = ParamType.RotarySwitch,
            ["fader"] = ParamType.Fader,
            ["meter"] = ParamType.Meter,
            ["stereoMeter"] = ParamType.StereoMeter,
            ["stereometer"] = ParamType.StereoMeter,
            ["grMeter"] = ParamType.GrMeter,
            ["grmeter"] = ParamType.GrMeter,
            ["stereoGrMeter"] = ParamType.StereoGrMeter,
            ["stereogrmeter"] = ParamType.StereoGrMeter,
            ["string"] = ParamType.String,
            ["toggleButton"] = ParamType.ToggleButton,
            ["togglebutton"] = ParamType.ToggleButton,
            ["comboBox"] = ParamType.ComboBox,
            ["combobox"] = ParamType.ComboBox,
            ["floatWithLed"] = ParamType.FloatWithLed,
            ["floatwithled"] = ParamType.FloatWithLed,
            ["empty"] = ParamType.Empty
        };

        public static Dictionary<int, ModelInfo> Parse(byte[] modelRepoPayload)
        {
            if (modelRepoPayload.Length == 0)
            {
                return [];
            }

            byte[] data = CompressionUtils.DecompressIfNeeded(modelRepoPayload);
            ModelRepoMessage repoMessage = ModelRepoMessage.Parser.ParseFrom(data);
            if (repoMessage.ModelRepoPayload.IsEmpty)
            {
                return [];
            }

            byte[] repoBytes = CompressionUtils.DecompressIfNeeded(repoMessage.ModelRepoPayload.ToByteArray());
            string? xml = ExtractXmlFromTar(repoBytes);
            if (string.IsNullOrWhiteSpace(xml))
            {
                return [];
            }

            return ParseModelRepoXml(xml);
        }

        private static string? ExtractXmlFromTar(byte[] tarData)
        {
            int offset = 0;
            while (offset + 512 <= tarData.Length)
            {
                string name = ReadNullTerminatedString(tarData, offset, 100);
                if (string.IsNullOrEmpty(name))
                {
                    break;
                }

                string sizeStr = ReadNullTerminatedString(tarData, offset + 124, 12).Trim();
                long fileSize = 0;
                if (!string.IsNullOrEmpty(sizeStr))
                {
                    fileSize = Convert.ToInt64(sizeStr, 8);
                }

                int dataOffset = offset + 512;
                if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && fileSize > 0)
                {
                    int length = (int)Math.Min(fileSize, tarData.Length - dataOffset);
                    return Encoding.UTF8.GetString(tarData, dataOffset, length);
                }

                int dataBlocks = (int)((fileSize + 511) / 512);
                offset = dataOffset + dataBlocks * 512;
            }

            return null;
        }

        private static string ReadNullTerminatedString(byte[] data, int offset, int maxLength)
        {
            int end = offset;
            int limit = Math.Min(offset + maxLength, data.Length);
            while (end < limit && data[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static Dictionary<int, ModelInfo> ParseModelRepoXml(string xml)
        {
            Dictionary<int, ModelInfo> models = new();
            XmlReaderSettings settings = new()
            {
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);

            string currentCategory = string.Empty;
            int? currentModelId = null;
            string currentModelName = string.Empty;
            List<ParamDef> currentParams = new();

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "Category":
                            currentCategory = reader.GetAttribute("name") ?? string.Empty;
                            break;
                        case "Model":
                            currentModelId = reader.GetAttribute("id")?.ToIntOrNull();
                            currentModelName = reader.GetAttribute("name") ?? string.Empty;
                            currentParams = [];
                            break;
                        case "Parameter":
                        case "Param":
                            string paramName = reader.GetAttribute("name") ?? string.Empty;
                            float min = reader.GetAttribute("min").ToFloatOrDefault();
                            float max = reader.GetAttribute("max").ToFloatOrDefault(1f);
                            string typeStr = reader.GetAttribute("type") ?? string.Empty;
                            ParamType paramType = ParseParamType(typeStr);
                            if (!string.IsNullOrEmpty(paramName))
                            {
                                currentParams.Add(new ParamDef
                                {
                                    Name = paramName,
                                    Min = min,
                                    Max = max,
                                    ParamType = paramType
                                });
                            }
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Name == "Category")
                    {
                        currentCategory = string.Empty;
                    }
                    else if (reader.Name == "Model")
                    {
                        if (currentModelId.HasValue)
                        {
                            models[currentModelId.Value] = new ModelInfo
                            {
                                Name = currentModelName,
                                Category = currentCategory,
                                ParamDefs = currentParams.ToList()
                            };
                        }
                        currentModelId = null;
                    }
                }
            }

            return models;
        }

        private static ParamType ParseParamType(string typeStr)
        {
            if (TypeStringMap.TryGetValue(typeStr, out ParamType mapped))
            {
                return mapped;
            }

            if (int.TryParse(typeStr, out int typeId))
            {
                return Enum.IsDefined(typeof(ParamType), typeId) ? (ParamType)typeId : ParamType.Unknown;
            }

            return ParamType.Unknown;
        }

        private static int? ToIntOrNull(this string? value)
        {
            if (int.TryParse(value, out int parsed))
            {
                return parsed;
            }

            return null;
        }

        private static float ToFloatOrDefault(this string? value, float fallback = 0f)
        {
            if (float.TryParse(value, out float parsed))
            {
                return parsed;
            }

            return fallback;
        }
    }
}
