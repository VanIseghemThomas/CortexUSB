using Google.Protobuf;
using CortexProtobufV2;

namespace OpenCortex.CortexUSB.Protocol
{
    /// <summary>
    /// Protobuf message encoding utilities for building protocol messages.
    /// Uses generated protobuf classes from Preset.proto and ProductionAutomation.proto.
    /// </summary>
    public static class ProtobufBuilder
    {
        /// <summary>
        /// Build a Scene message (type 13).
        /// Format: {action=UPDATE, selected_scene=scene_index}
        /// Scene index: 0-7
        /// </summary>
        public static byte[] BuildSceneMessage(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(sceneIndex), "Scene index must be 0-7");
            }

            SceneMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                SelectedScene = (uint)sceneIndex
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Mode message (type 14).
        /// Format: {action=UPDATE, mode=mode}
        /// Mode: 0=Preset, 1=Scene, 2=Stomp
        /// </summary>
        public static byte[] BuildModeMessage(int mode)
        {
            if (mode < 0 || mode > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), "Mode must be 0 (Preset), 1 (Scene), or 2 (Stomp)");
            }

            ModeMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                Mode = (uint)mode
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a state query message for any message type.
        /// Format: {action=READ}
        /// Used to query current scene, mode, tempo, etc.
        /// This returns an empty message with action=READ. The message type is determined by the context.
        /// </summary>
        public static byte[] BuildStateQuery()
        {
            // For state queries, we just need action=READ
            // The Python implementation uses field 1 = 3 (READ)
            // Let's use SceneMessage as a generic container since it has the action field
            SceneMessage message = new()
            {
                Action = MessageAction.Types.Enum.Read
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a SetlistPosition message (type 2) for preset switching.
        /// Format: {action=UPDATE, folder_key=setlist_path, position=preset_index, is_factory=is_factory}
        /// </summary>
        public static byte[] BuildSetlistPositionMessage(string setlistPath, int presetIndex, bool isFactory)
        {
            if (presetIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(presetIndex), "Preset index must be >= 0");
            }

            SetlistPositionMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                FolderKey = setlistPath,
                Position = (uint)presetIndex,
                IsFactory = isFactory
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Model id of the per-preset TempoControl block.
        /// </summary>
        private const uint TempoControlModelId = 25000;

        /// <summary>
        /// Build a Grid message (type 1) setting the preset's TEMPO parameter.
        ///
        /// The per-preset tempo lives in BinaryPreset.tempoProgramData[0] (the
        /// TempoControl block, model 25000) and is written as a Grid UPDATE — NOT
        /// as a GlobalTempo message (type 33), which carries the device's own copy
        /// and is the wrong target for "set this preset's BPM". The wire value is
        /// the normalized float (bpm - 40) / 200 over the measured 40..240 span;
        /// the catalog range is a placeholder, so the span was measured on
        /// hardware (reference: pyquadcortex set_tempo_param, 4.0.1 / d14e).
        /// </summary>
        public static byte[] BuildTempoGridMessage(int bpm)
        {
            if (bpm < 40 || bpm > 240)
            {
                throw new ArgumentOutOfRangeException(nameof(bpm), "BPM must be between 40-240");
            }

            BinaryPreset preset = new();

            Model tempo = new()
            {
                Hash = TempoControlModelId
            };

            Param param = new()
            {
                Index = 0 // TEMPO
            };

            param.ParamValues.Add(new ParamValue
            {
                FloatValue = (float)((bpm - 40.0) / 200.0)
            });

            tempo.Params.Add(param);
            preset.TempoProgramData.Add(tempo);

            GridMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                Preset = preset
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) for toggling block bypass for a specific scene.
        /// Format: GridMessage { action=UPDATE, preset=BinaryPreset { bypass=[...] } }
        /// 
        /// Uses BinaryPreset.bypass field (field 18):
        /// - bypass[row_index].colBypass[col_index].sceneBypass[scene_index].bypass = true/false
        /// </summary>
        public static byte[] BuildGridBypassMessage(int rowIndex, int columnIndex, int sceneIndex, bool bypassed)
        {
            if (rowIndex < 0 || rowIndex > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");
            }

            if (columnIndex < 0 || columnIndex > 11)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column index must be 0-11");
            }

            if (sceneIndex < 0 || sceneIndex > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(sceneIndex), "Scene index must be 0-7");
            }

            // Create a BinaryPreset with bypass information for one block in one scene
            BinaryPreset preset = new();

            Bypass bypass = new()
            {
                Row = (uint)rowIndex
            };

            ColBypass colBypass = new()
            {
                Column = (uint)columnIndex,
                SceneMode = true
            };

            // Add SceneBypass entries for all 8 scenes, setting the target scene's bypass state
            for (int i = 0; i < 8; i++)
            {
                colBypass.SceneBypass.Add(new SceneBypass
                {
                    Bypass = (i == sceneIndex) && bypassed
                });
            }

            bypass.ColBypass.Add(colBypass);
            preset.Bypass.Add(bypass);

            GridMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                Preset = preset
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) for toggling block bypass for current scene (simplified).
        /// Format: GridMessage { action=UPDATE, preset=BinaryPreset { bypass=[...] } }
        /// This uses a simpler approach that sets bypass for the current scene context.
        /// </summary>
        public static byte[] BuildGridBypassMessage(int rowIndex, int columnIndex, bool bypassed)
        {
            if (rowIndex < 0 || rowIndex > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");
            }

            if (columnIndex < 0 || columnIndex > 11)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column index must be 0-11");
            }

            // Create a BinaryPreset with bypass information for one block
            BinaryPreset preset = new();

            Bypass bypass = new()
            {
                Row = (uint)rowIndex
            };

            // sceneMode is intentionally left unset (not host-writable per
            // pyquadcortex — explicitly sending it, even false, risks the whole
            // write being ignored). A single sceneBypass entry with no sceneMode
            // is the device's own "global bypass" shape.
            ColBypass colBypass = new()
            {
                Column = (uint)columnIndex
            };

            colBypass.SceneBypass.Add(new SceneBypass
            {
                Bypass = bypassed  // Direct bypass state
            });

            bypass.ColBypass.Add(colBypass);
            preset.Bypass.Add(bypass);

            GridMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                Preset = preset
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) for setting a block parameter for current scene.
        /// Format: GridMessage { action=UPDATE, preset=BinaryPreset { chains=[...] } }
        /// 
        /// Creates a minimal BinaryPreset with one Chain containing one Model with one Param.
        /// - Chain.row = row_index
        /// - Model.column = column_index  
        /// - Param.index = param_index, param_values = [ParamValue { float_value }]
        /// </summary>
        public static byte[] BuildGridParamMessage(int rowIndex, int columnIndex, int paramIndex, float value)
        {
            if (rowIndex < 0 || rowIndex > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");
            }

            if (columnIndex < 0 || columnIndex > 11)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column index must be 0-11");
            }

            // Create a minimal BinaryPreset with the parameter change
            BinaryPreset preset = new();

            Chain chain = new()
            {
                Row = (uint)rowIndex
            };

            Model model = new()
            {
                Column = (uint)columnIndex
            };

            Param param = new()
            {
                Index = (uint)paramIndex
            };
        
            param.ParamValues.Add(new ParamValue
            {
                FloatValue = value
            });

            model.Params.Add(param);
            chain.Models.Add(model);
            preset.Chains.Add(chain);

            GridMessage message = new()
            {
                Action = MessageAction.Types.Enum.Update,
                Preset = preset
            };

            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) placing a block in a grid cell.
        /// Format: GridMessage { action=UPDATE, preset={ chains=[{ row, models=[{ column, hash }] }] } }
        /// Row/column-keyed sparse update — the ONLY shape that persists an edit.
        /// Placement can be refused for DSP capacity with no error; verify by Grid echo.
        /// </summary>
        public static byte[] BuildGridSetBlockMessage(int rowIndex, int columnIndex, uint modelHash)
        {
            if (rowIndex < 0 || rowIndex > 3) throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");
            if (columnIndex < 0 || columnIndex > 7) throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column index must be 0-7");
            if (modelHash == 0) throw new ArgumentOutOfRangeException(nameof(modelHash), "Model hash must be non-zero");

            BinaryPreset preset = new();
            Chain chain = new() { Row = (uint)rowIndex };
            chain.Models.Add(new Model { Column = (uint)columnIndex, Hash = modelHash });
            preset.Chains.Add(chain);

            return new GridMessage { Action = MessageAction.Types.Enum.Update, Preset = preset }.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) removing a block from a grid cell.
        /// Format: GridMessage { action=DELETE, preset={ chains=[{ row, models=[{ column, hash: 0 }] }] } }
        /// The DELETE action marks the removal; an UPDATE with hash:0 is ignored by firmware.
        /// </summary>
        public static byte[] BuildGridRemoveBlockMessage(int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex > 3) throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");
            if (columnIndex < 0 || columnIndex > 7) throw new ArgumentOutOfRangeException(nameof(columnIndex), "Column index must be 0-7");

            BinaryPreset preset = new();
            Chain chain = new() { Row = (uint)rowIndex };
            chain.Models.Add(new Model { Column = (uint)columnIndex, Hash = 0 });
            preset.Chains.Add(chain);

            return new GridMessage { Action = MessageAction.Types.Enum.Delete, Preset = preset }.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) re-pointing a grid row's input.
        /// Format: GridMessage { action=UPDATE, preset={ chains=[{ row, in_portid }] } }
        /// The ONLY shape that moves an input on the wire (a full-preset write does nothing).
        /// </summary>
        public static byte[] BuildGridChainInputMessage(int rowIndex, uint inPortId)
        {
            if (rowIndex < 0 || rowIndex > 3) throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");

            BinaryPreset preset = new();
            preset.Chains.Add(new Chain { Row = (uint)rowIndex, InPortid = inPortId });

            return new GridMessage { Action = MessageAction.Types.Enum.Update, Preset = preset }.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) re-pointing a grid row's output.
        /// Format: GridMessage { action=UPDATE, preset={ chains=[{ row, out_portid }] } }
        /// Port id 19 (MULTIPLE) is the Multi-Out destination.
        /// </summary>
        public static byte[] BuildGridChainOutputMessage(int rowIndex, uint outPortId)
        {
            if (rowIndex < 0 || rowIndex > 3) throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index must be 0-3");

            BinaryPreset preset = new();
            preset.Chains.Add(new Chain { Row = (uint)rowIndex, OutPortid = outPortId });

            return new GridMessage { Action = MessageAction.Types.Enum.Update, Preset = preset }.ToByteArray();
        }

        /// <summary>
        /// Build a Grid message (type 1) branching a row into its parallel lane.
        /// Format: GridMessage { action=UPDATE, preset={ chains=[{ row, split_control_points=[{ split, mix }] }] } }
        /// Pass mixColumn = -1 for a branch that never rejoins; (-1, -1) clears the branch.
        /// Row must be even (0 or 2) — every even row already carries a dormant splitter.
        /// </summary>
        public static byte[] BuildGridSplitMessage(int rowIndex, int splitColumn, int mixColumn)
        {
            if (rowIndex is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(rowIndex), "Split row must be 0 or 2");

            BinaryPreset preset = new();
            Chain chain = new() { Row = (uint)rowIndex };
            chain.SplitControlPoints.Add(new SplitControlPoints { Split = splitColumn, Mix = mixColumn });
            preset.Chains.Add(chain);

            return new GridMessage { Action = MessageAction.Types.Enum.Update, Preset = preset }.ToByteArray();
        }

        /// <summary>
        /// Build a File message (type 4) saving the preset currently on the grid ("Save As").
        /// Format: FileMessage { action=CREATE, type=0, folder={ key=<setlist path>, is_factory=false,
        /// files=[{ index=<linear slot>, name, instrument }] } }
        /// NO preset payload — the device saves the grid it already has. The device de-duplicates
        /// names on collision and truncates to 20 chars; read the slot back for the final name.
        /// </summary>
        public static byte[] BuildSavePresetMessage(string setlistPath, int slotIndex, string name, int instrument)
        {
            if (string.IsNullOrWhiteSpace(setlistPath)) throw new ArgumentException("Setlist path is required", nameof(setlistPath));
            if (slotIndex < 0 || slotIndex > 255) throw new ArgumentOutOfRangeException(nameof(slotIndex), "Slot index must be 0-255");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Preset name is required", nameof(name));

            FileMessage message = new() { Type = 0 };
            message.Folder.Key = setlistPath;
            message.Folder.IsFactory = false;
            message.Folder.Files.Add(new ProductData
            {
                Index = slotIndex,
                Name = name,
                Instrument = instrument
            });

            return message.ToByteArray();
        }

        /// <summary>
        /// Convert a QC slot name like "28C" to its linear wire position.
        /// position = (bank - 1) * 8 + letterIndex, A=0..H=7, banks 1-32 (a setlist holds 256 slots).
        /// </summary>
        public static int SlotToPosition(string slot)
        {
            string s = slot.Trim().ToUpperInvariant();
            if (s.Length < 2 || !int.TryParse(s[..^1], out int bank) || s[^1] is < 'A' or > 'H')
            {
                throw new ArgumentException($"Slot must look like '28C' (bank number + letter A-H): {slot}", nameof(slot));
            }
            if (bank < 1 || bank > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), $"Bank must be 1-32 (a setlist holds 256 slots): {slot}");
            }
            return (bank - 1) * 8 + (s[^1] - 'A');
        }

        // ─── Phase 4: Global EQ / Master Volume / Tuner ────────────────────
        // Wire shapes verified against pyquadcortex (client.py set_global_eq*,
        // set_master_volume, set_tuner_input/mute), CorOS 4.0.1 / firmware d14e.

        /// <summary>Number of Global EQ bands (the unit's own numbering, 1-5).</summary>
        public const int GlobalEqBands = 5;

        /// <summary>Wire parameters per band: GAIN, FREQUENCY, Q, TYPE, ENABLED (bypass, inverted).</summary>
        public const int GlobalEqBandStride = 5;

        public const int GlobalEqOutLevelIndex = 25;
        public const int GlobalEqOut12Index = 26;
        public const int GlobalEqOut34Index = 27;

        /// <summary>
        /// Build a GlobalEQ message (type 38) writing one parameter by its wire
        /// index. Writes are sparse — only the given index is sent, the rest of
        /// the 28-parameter list is left untouched.
        /// </summary>
        public static byte[] BuildGlobalEqParamMessage(int parameterIndex, float value)
        {
            GlobalEQMessage message = new() { Action = MessageAction.Types.Enum.Update };
            message.Parameters.Add(new GlobalEQParameter { ParameterIndex = parameterIndex, Value = value });
            return message.ToByteArray();
        }

        /// <summary>
        /// Wire index for one of a band's five controls (band is 1-5, matching the
        /// unit's own numbering). Layout per band: 0=GAIN, 1=FREQUENCY, 2=Q, 3=TYPE,
        /// 4=ENABLED (1.0=active, 0.0=bypassed — inverted from the message-level
        /// Bypassed flag). Every value is normalized 0..1; gain 0.5=0dB, 0.75=+6dB
        /// on the -12..+12dB range.
        /// </summary>
        public static int GlobalEqBandParamIndex(int band, int offset)
        {
            if (band < 1 || band > GlobalEqBands)
            {
                throw new ArgumentOutOfRangeException(nameof(band), $"Band must be 1-{GlobalEqBands}");
            }
            return (band - 1) * GlobalEqBandStride + offset;
        }

        /// <summary>
        /// Build a GlobalEQ message (type 38) toggling the whole EQ on/off.
        /// Bypassed=true is EQ OFF (the unit's own On/Off control is the inverse).
        /// </summary>
        public static byte[] BuildGlobalEqBypassMessage(bool bypassed)
        {
            GlobalEQMessage message = new() { Action = MessageAction.Types.Enum.Update, Bypassed = bypassed };
            return message.ToByteArray();
        }

        /// <summary>
        /// Build a MasterVolume message (type 17). Volume is normalized 0..1 (the
        /// unit displays round(volume * 100)); the write lands on its own and is
        /// a real level change downstream of stored port levels.
        /// </summary>
        public static byte[] BuildMasterVolumeMessage(float volume)
        {
            if (volume < 0f || volume > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(volume), "Master volume is normalized 0..1");
            }
            MasterVolumeMessage message = new() { Action = MessageAction.Types.Enum.Update, Volume = volume };
            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Tuner message (type 6) selecting the input the tuner reads from.
        ///
        /// WARNING (pyquadcortex-documented device behavior): any write to this
        /// message ENGAGES the tuner invisibly — nothing changes on screen. If the
        /// mute preference (<see cref="BuildTunerMuteMessage"/>) is already true,
        /// the outputs go silent with no visible cause, and the only lossless
        /// release is a person opening and closing the tuner on the unit itself.
        /// See <see cref="ProtocolService.RestoreAudioAsync"/> for the host-side
        /// escape hatch (clears the mute preference, leaves the unit engaged but
        /// audible).
        /// </summary>
        public static byte[] BuildTunerInputMessage(int inputPortId)
        {
            TunerMessage message = new() { Action = MessageAction.Types.Enum.Update, InputPortId = inputPortId };
            return message.ToByteArray();
        }

        /// <summary>
        /// Build a Tuner message (type 6) setting the mute-while-tuning preference.
        /// Same invisible-engage warning as <see cref="BuildTunerInputMessage"/> —
        /// writing mute=true here silences the outputs the instant it lands.
        /// </summary>
        public static byte[] BuildTunerMuteMessage(bool mute)
        {
            TunerMessage message = new() { Action = MessageAction.Types.Enum.Update, Mute = mute };
            return message.ToByteArray();
        }

        // Legacy manual encoding helpers removed — use generated protobuf message classes instead.
    }
}
