using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SDVRadiance;

namespace ShareCodeHarness
{
    /// <summary>
    /// What a share code has to survive, checked without opening the game.
    ///
    /// <para>The part worth the effort is the LAST test. A code written by a release that knows a
    /// setting this one has never heard of must still work here, with the unknown setting stepped
    /// over and counted. That case cannot be produced by asking the writer for it, so the bytes are
    /// built here by hand from the format as it is documented: an independent second opinion, which
    /// is the only kind worth having about a format two releases have to agree on.</para>
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            RoundTripKeepsEveryKind();
            OnlyTheDifferencesTravel();
            TypedCarelesslyStillReads();
            OneWrongCharacterIsCaught();
            SomebodyElsesTextIsRefused();
            AScopeFromTheFutureIsRefusedByName();
            ASettingFromANewerReleaseIsSteppedOver();
            AChipRemembersItsColourTable();
            ThePrefixIsForgivenAndTheScopeIsChecked();
            TheReceiversOwnChangesDoNotSurvive();
            ASwitchCarriesItsPosition();
            AWholeSetupTravelsOnlyWhenAsked();
            ATableTheSenderOwnsIsNotForcedOnAnybody();
            HowLongTheyActuallyAre();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "all checks passed" : $"{_failures} check(s) FAILED");
            return _failures == 0 ? 1 - 1 : 1;
        }

        private static void Check(string what, bool ok, string saw = "")
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(ok || saw.Length == 0 ? "" : "   " + saw)}");
            if (!ok)
                _failures++;
        }

        /// <summary>A look with one of every shape a setting can have: a switch, a dial that fits a
        /// hundredths byte, a dial that does not, a negative dial, a whole number, a choice from a
        /// list, and a name.</summary>
        private static ModConfig ALookWorthSharing()
        {
            // Clamped like any config the mod is actually holding: Clamp also RETIRES values, and a
            // look compared against an unclamped copy would fail on a value the mod itself rewrites.
            var look = new ModConfig
            {
                BloomEnabled = true,
                BloomIntensity = 0.62f,
                BloomThreshold = 0.55f,
                ColorGradeEnabled = true,
                ColorGradeSaturation = 1.18f,
                ColorGradeTemperature = -0.125f,
                ColorGradeLut = "Golden Hour",
                ColorGradeLutAmount = 0.4f,
                FogEnabled = true,
                FogDensity = 0.45f,
                LightingNightDarkness = 0.8f,
                ShadowSunBearing = 137.5f,
                WaterReflectStyle = WaterReflectionStyle.Choppy,
                TiltShiftMode = TiltShiftFocus.Radial,
                SheetUpscaleSoftKernel = SoftSmoothingKernel.Xbr,
                SheetUpscaleKernelCharacters = FamilyKernelChoice.Epx,
                RenderScale = 0.85f,
                RenderScaleAuto = true,
            };
            look.Clamp();
            return look;
        }

        private static void RoundTripKeepsEveryKind()
        {
            Console.WriteLine("a code says exactly what was shared");
            ModConfig shared = ALookWorthSharing();
            string code = ShareCode.Write(shared);
            Console.WriteLine($"       {code}   ({code.Length} characters)");

            var landing = new ModConfig();
            bool read = ShareCode.TryRead(code, landing, out ShareCodeReading reading, out string problem);
            Check("the code reads", read, problem);
            if (!read)
                return;
            ShareCode.Apply(reading, landing);

            foreach ((int _, string property, bool isPerformance) in ShareCode.KnownSettings)
            {
                if (isPerformance)
                    continue;      // a look code leaves those behind on purpose; checked below
                string wanted = Read(shared, property);
                string got = Read(landing, property);
                if (wanted != got)
                {
                    Check($"{property} arrives as it was sent", false, $"sent {wanted}, arrived {got}");
                    return;
                }
            }
            Check("every look setting arrives as it was sent", true);
            Check("nothing was left unknown", reading.SettingsThisReleaseCannotUse == 0);
        }

        private static void OnlyTheDifferencesTravel()
        {
            Console.WriteLine("a code carries the differences and nothing else");
            string nothingChanged = ShareCode.Write(new ModConfig());
            Check("a fresh install writes a code with no settings in it", nothingChanged.Length < 20, nothingChanged);

            var landing = new ModConfig();
            ShareCode.TryRead(nothingChanged, landing, out ShareCodeReading reading, out _);
            Check("and that code changes nothing", reading.Changes.Count == 0);

            string oneDial = ShareCode.Write(new ModConfig { FogDensity = 0.45f });
            Check("one dial is a short code", oneDial.Length <= 24, oneDial);

            // A slider dragged back to where it started holds a hair off the default.
            float fogDefault = new ModConfig().FogDensity;
            string draggedBack = ShareCode.Write(new ModConfig { FogDensity = fogDefault + 0.00003f }, ShareCodeScope.Look, out int carriedBack);
            Check("a dial dragged back to its default travels as nothing", carriedBack == 0 && draggedBack == nothingChanged, draggedBack);

            ShareCode.Write(ALookWorthSharing(), ShareCodeScope.Look, out int counted);
            ShareCode.TryRead(ShareCode.Write(ALookWorthSharing()), new ModConfig(), out ShareCodeReading countedBack, out _);
            Check("the count the writer gives is what the code holds", counted == countedBack.Changes.Count,
                  $"{counted} against {countedBack.Changes.Count}");
        }

        private static void TypedCarelesslyStillReads()
        {
            Console.WriteLine("a code survives the trip through a chat window");
            string code = ShareCode.Write(ALookWorthSharing());
            string mangled = "  " + code.ToLowerInvariant().Replace("-", "\n  ") + "  ";
            var landing = new ModConfig();
            bool read = ShareCode.TryRead(mangled, landing, out ShareCodeReading reading, out string problem);
            Check("lower case, line breaks and stray spaces are forgiven", read, problem);
            if (!read)
                return;
            var plain = new ModConfig();
            ShareCode.TryRead(code, plain, out ShareCodeReading straight, out _);
            Check("and it says the same thing as the tidy one", reading.Changes.Count == straight.Changes.Count);
        }

        private static void OneWrongCharacterIsCaught()
        {
            Console.WriteLine("a code that lost a character says so");
            string code = ShareCode.Write(ALookWorthSharing());
            int caught = 0, tried = 0;
            for (int at = ShareCode.Prefix(ShareCodeScope.Look).Length + 1; at < code.Length; at++)
            {
                if (code[at] == '-')
                    continue;
                tried++;
                string bent = code[..at] + code[(at + 1)..];
                if (!ShareCode.TryRead(bent, new ModConfig(), out _, out string problem) && problem == "checksum")
                    caught++;
            }
            Check($"a dropped character is caught ({caught} of {tried})", caught == tried);
        }

        private static void SomebodyElsesTextIsRefused()
        {
            Console.WriteLine("text that is not a code of ours is turned away by name");
            Check("a sentence", !ShareCode.TryRead("have you tried turning it off", new ModConfig(), out _, out string a) && a == "notours", a);
            Check("an empty box", !ShareCode.TryRead("", new ModConfig(), out _, out string b) && b == "notours", b);
        }

        private static void AScopeFromTheFutureIsRefusedByName()
        {
            Console.WriteLine("a kind of code this release cannot open is named, not guessed at");
            bool aScope = ShareCode.TryRead("RAD1X-ABCDE-FGHJK", new ModConfig(), out _, out string scopeProblem);
            Check("a scope that does not exist yet is refused as a scope, not as a typo",
                  !aScope && scopeProblem == "scope", scopeProblem);
            bool aFormat = ShareCode.TryRead("RAD9L-ABCDE-FGHJK", new ModConfig(), out _, out string formatProblem);
            Check("and so is a format from further ahead than this one", !aFormat && formatProblem == "scope", formatProblem);
        }

        /// <summary>The promise that matters most: a code from a release that knows more settings
        /// than this one still works, minus the settings this one has never heard of.</summary>
        private static void ASettingFromANewerReleaseIsSteppedOver()
        {
            Console.WriteLine("a code from a newer release still works here");
            var payload = new List<byte>();
            // A setting this release knows: fog density, number 15, as a hundredths byte.
            payload.Add((byte)((0 << 5) | 15));
            payload.Add(45);
            // A setting from the future, number 3000, two bytes of value. Written the way the
            // format says an entry is written, without asking the writer to do it.
            payload.Add((byte)(0x80 | (1 << 5) | (3000 & 0x1F)));
            payload.Add((byte)(3000 >> 5));
            payload.Add(0x10);
            payload.Add(0x27);
            // Another one this release knows, AFTER the unknown one: this is what proves the
            // unknown entry was stepped over by exactly its own length.
            payload.Add((byte)((0 << 5) | 2));
            payload.Add(62);

            string code = ShareCode.Prefix(ShareCodeScope.Look) + "-" + Base32(WithLengthAndCheck(payload));
            var landing = new ModConfig();
            bool read = ShareCode.TryRead(code, landing, out ShareCodeReading reading, out string problem);
            Check("the code reads", read, problem);
            if (!read)
                return;
            ShareCode.Apply(reading, landing);
            Check("the setting before the unknown one landed", Read(landing, nameof(ModConfig.FogDensity)) == "0.45",
                  Read(landing, nameof(ModConfig.FogDensity)));
            Check("the setting after the unknown one landed", Read(landing, nameof(ModConfig.BloomIntensity)) == "0.62",
                  Read(landing, nameof(ModConfig.BloomIntensity)));
            Check("the unknown setting was counted, not dropped in silence", reading.SettingsThisReleaseCannotUse == 1,
                  reading.SettingsThisReleaseCannotUse.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A saved look, the backup a kept code leaves included, puts its colour table back.</summary>
        private static void AChipRemembersItsColourTable()
        {
            Console.WriteLine("a saved look brings its colour table back");
            var config = new ModConfig { ColorGradeEnabled = true, ColorGradeLut = "moonlit" };
            NamedProfile chip = config.CaptureProfile("before");
            config.ColorGradeLut = "warm-film";
            Check("the chip no longer matches once the table changes", !config.MatchesProfile(chip));
            config.ApplyProfile(chip);
            Check("loading it puts moonlit back", config.ColorGradeLut == "moonlit", config.ColorGradeLut);
            var noTable = new ModConfig { ColorGradeLut = "" }.CaptureProfile("plain");
            config.ApplyProfile(noTable);
            Check("and a chip saved with no table takes the table off", config.ColorGradeLut == "", config.ColorGradeLut);
            chip.Values!.Remove(nameof(ModConfig.ColorGradeLut));
            config.ColorGradeLut = "verdant";
            config.ApplyProfile(chip);
            Check("an older chip that never recorded one leaves the table alone", config.ColorGradeLut == "verdant", config.ColorGradeLut);
        }

        /// <summary>A prefix copied off a photograph reads, and a scope letter typed wrong is caught
        /// rather than believed.</summary>
        private static void ThePrefixIsForgivenAndTheScopeIsChecked()
        {
            Console.WriteLine("the front of a code is read as carefully as the rest");
            string code = ShareCode.Write(ALookWorthSharing());
            string photographed = "radIl" + code[5..];
            bool read = ShareCode.TryRead(photographed, new ModConfig(), out _, out string problem);
            Check("RADIL for RAD1L reads", read, problem);
            string swapped = code[..4] + "F" + code[5..];
            bool swappedRead = ShareCode.TryRead(swapped, new ModConfig(), out _, out string swappedProblem);
            Check("an F typed for the L is caught as a slip", !swappedRead && swappedProblem == "checksum", swappedProblem);
        }

        /// <summary>A code is the sender's whole look, not their changes laid over the receiver's:
        /// a setting the receiver moved and the sender left alone goes back to the default.</summary>
        private static void TheReceiversOwnChangesDoNotSurvive()
        {
            Console.WriteLine("trying on a code gives the sender's look, not a mix of the two");
            var sender = new ModConfig { ColorGradeEnabled = true, ColorGradeSaturation = 1.3f };
            sender.Clamp();
            var receiver = new ModConfig { BloomEnabled = !new ModConfig().BloomEnabled, FogEnabled = true, FogDensity = 0.9f, RenderScale = 0.7f };
            receiver.Clamp();
            ShareCode.TryRead(ShareCode.Write(sender), receiver, out ShareCodeReading reading, out _);
            ShareCode.Apply(reading, receiver);
            foreach ((int _, string property, bool isPerformance) in ShareCode.KnownSettings)
            {
                if (isPerformance)
                    continue;
                if (Read(sender, property) != Read(receiver, property))
                {
                    Check($"{property} is the sender's", false, $"sender {Read(sender, property)}, receiver {Read(receiver, property)}");
                    return;
                }
            }
            Check("every look setting is the sender's", true);
            Check("and the ones that went back to the default say so", reading.Changes.Exists(change => change.ReturnsToDefault));
            Check("a look code leaves the receiver's performance settings alone", receiver.RenderScale == 0.7f,
                  receiver.RenderScale.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A switch carries its position, not "the other one": a code read by a release
        /// that changed the switch's default must still set it the way the sender had it. Built
        /// by hand in both positions, so it holds whatever today's default happens to be.</summary>
        private static void ASwitchCarriesItsPosition()
        {
            Console.WriteLine("a switch arrives in the position it was sent in");
            foreach (bool position in new[] { true, false })
            {
                // Bloom on or off, number 0, a one-byte value.
                var payload = new List<byte> { (byte)((0 << 5) | 0), position ? (byte)1 : (byte)0 };
                string code = ShareCode.Prefix(ShareCodeScope.Look) + "-" + Base32(WithLengthAndCheck(payload));
                var landing = new ModConfig { BloomEnabled = !position };
                bool read = ShareCode.TryRead(code, landing, out ShareCodeReading reading, out string problem);
                if (read)
                    ShareCode.Apply(reading, landing);
                Check($"bloom sent {(position ? "on" : "off")} lands {(position ? "on" : "off")}",
                      read && landing.BloomEnabled == position, problem + " " + landing.BloomEnabled);
            }
        }

        /// <summary>The performance group is the one that can make a game feel worse rather than
        /// look different, so it travels only when the person sharing chose to send their whole
        /// setup, and it is marked when it arrives so it can be shown apart from the look.</summary>
        private static void AWholeSetupTravelsOnlyWhenAsked()
        {
            Console.WriteLine("performance settings travel only in a whole-setup code");
            ModConfig shared = ALookWorthSharing();

            string look = ShareCode.Write(shared);
            var afterLook = new ModConfig();
            ShareCode.TryRead(look, afterLook, out ShareCodeReading lookReading, out _);
            ShareCode.Apply(lookReading, afterLook);
            Check("a look code leaves the render scale at home",
                  Read(afterLook, nameof(ModConfig.RenderScale)) == Read(new ModConfig(), nameof(ModConfig.RenderScale)),
                  Read(afterLook, nameof(ModConfig.RenderScale)));
            Check("and none of its changes is marked as costing frames",
                  !lookReading.Changes.Exists(change => change.IsPerformance));
            Check("and it says it is a look", lookReading.Scope == ShareCodeScope.Look);

            string whole = ShareCode.Write(shared, ShareCodeScope.Everything);
            var afterWhole = new ModConfig();
            ShareCode.TryRead(whole, afterWhole, out ShareCodeReading wholeReading, out _);
            ShareCode.Apply(wholeReading, afterWhole);
            Check("a whole-setup code brings it", Read(afterWhole, nameof(ModConfig.RenderScale)) == "0.85",
                  Read(afterWhole, nameof(ModConfig.RenderScale)));
            Check("and marks it, so it can be shown apart from the look",
                  wholeReading.Changes.FindAll(change => change.IsPerformance).Count == 2);
            Check("and it says what it is", wholeReading.Scope == ShareCodeScope.Everything);
            Check("the two codes are told apart by their fifth character",
                  look[..4] == whole[..4] && look[4] != whole[4], look[..5] + " / " + whole[..5]);
        }

        /// <summary>A colour table that shipped with the mod is on every install, so it travels as
        /// its place in that list and always lands. One the sender made themselves travels as its
        /// name, and lands only where that file is: the check for the file is the game's to make,
        /// so here the two paths are told apart by what they cost and by what they carry.</summary>
        private static void ATableTheSenderOwnsIsNotForcedOnAnybody()
        {
            Console.WriteLine("a colour table travels by its place when everyone has it, by name when not");
            var shipped = new ModConfig { ColorGradeEnabled = true, ColorGradeLut = "autumn-gold" };
            var theirOwn = new ModConfig { ColorGradeEnabled = true, ColorGradeLut = "somebody-elses-look" };
            string shippedCode = ShareCode.Write(shipped), theirOwnCode = ShareCode.Write(theirOwn);
            Check("a shipped table costs a byte, not its name", shippedCode.Length + 20 < theirOwnCode.Length,
                  $"{shippedCode.Length} against {theirOwnCode.Length} characters");

            var landing = new ModConfig();
            ShareCode.TryRead(shippedCode, landing, out ShareCodeReading reading, out _);
            ShareCode.Apply(reading, landing);
            Check("and it arrives by name at the other end",
                  Read(landing, nameof(ModConfig.ColorGradeLut)) == "autumn-gold",
                  Read(landing, nameof(ModConfig.ColorGradeLut)));

            var elsewhere = new ModConfig();
            ShareCode.TryRead(theirOwnCode, elsewhere, out ShareCodeReading theirs, out _);
            Check("a table of the sender's own carries its name, so it can be looked for",
                  theirs.Changes.Exists(change => change.NewValue == "somebody-elses-look")
                  || theirs.ColourTableNotHere == "somebody-elses-look",
                  theirs.ColourTableNotHere);
        }

        /// <summary>What a code actually costs, printed rather than asserted: the numbers are the
        /// point of the format and they belong where somebody changing it will see them move.</summary>
        private static void HowLongTheyActuallyAre()
        {
            Console.WriteLine("what a code costs");
            Show("nothing changed", new ModConfig());
            Show("one dial moved", new ModConfig { FogDensity = 0.45f });
            foreach (LookPreset preset in new[] { LookPreset.Cinematic, LookPreset.Vibrant, LookPreset.Subtle })
            {
                var config = new ModConfig();
                config.ApplyPreset(preset);
                Show(preset + " off the dropdown", config);
            }
            var handmade = new ModConfig
            {
                BloomEnabled = true, BloomIntensity = 0.7f, BloomThreshold = 0.5f,
                ColorGradeEnabled = true, ColorGradeStrength = 0.8f, ColorGradeContrast = 1.1f,
                ColorGradeSaturation = 1.2f, ColorGradeTemperature = 0.15f, ColorGradeBrightness = 1.05f,
                FogEnabled = true, FogDensity = 0.35f, VignetteStrength = 0.4f,
                LightingNightDarkness = 0.75f, GodRaysEnabled = true, GodRaysIntensity = 0.45f,
            };
            Show("a hand-tuned look, fifteen dials", handmade);
            Show("a look leaning on a shipped colour table", new ModConfig
            {
                ColorGradeEnabled = true, ColorGradeLut = "autumn-gold", ColorGradeLutAmount = 0.6f,
                ColorGradeSaturation = 1.1f, BloomIntensity = 0.55f,
            });
            Show("the sample look", ALookWorthSharing());
            Show("the same, whole setup", ALookWorthSharing(), ShareCodeScope.Everything);
        }

        private static void Show(string what, ModConfig config, ShareCodeScope scope = ShareCodeScope.Look)
        {
            config.Clamp();
            string code = ShareCode.Write(config, scope);
            ShareCode.TryRead(code, new ModConfig(), out ShareCodeReading reading, out _);
            Console.WriteLine($"     {code.Length,4} characters, {reading.Changes.Count,2} setting(s)   {what}");
            Console.WriteLine($"          {code}");
        }

        private static string Read(ModConfig config, string property)
        {
            var found = typeof(ModConfig).GetProperty(property);
            return found == null ? "?" : ShareCode.Spell(found.GetValue(config));
        }

        // A second, independent copy of the wrapping and the alphabet, so the test agrees with the
        // FORMAT rather than with the code that writes it.
        private static List<byte> WithLengthAndCheck(List<byte> payload, char scopeLetter = 'L')
        {
            // The length is one byte while that is enough, which every pasteable code is.
            var bytes = new List<byte> { (byte)payload.Count };
            bytes.AddRange(payload);
            // The check starts from the scope letter, so a look code cannot pass for a whole setup.
            int running = ((17 * 31) + scopeLetter) & 0xFF;
            foreach (byte value in bytes)
                running = ((running * 31) + value) & 0xFF;
            bytes.Add((byte)running);
            return bytes;
        }

        private static string Base32(List<byte> bytes)
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var built = new StringBuilder();
            int held = 0, bits = 0;
            foreach (byte value in bytes)
            {
                held = (held << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    built.Append(alphabet[(held >> (bits - 5)) & 0x1F]);
                    bits -= 5;
                }
            }
            if (bits > 0)
                built.Append(alphabet[(held << (5 - bits)) & 0x1F]);
            return built.ToString();
        }
    }
}
