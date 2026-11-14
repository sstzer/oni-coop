namespace OniCoop.Net
{
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public sealed class ModEntry
    {
        public string staticId = "";
        public string title = "";
        public long version;

        public void Write(BinaryWriter w) { w.Write(staticId); w.Write(title); w.Write(version); }
        public static ModEntry Read(BinaryReader r) => new ModEntry { staticId = r.ReadString(), title = r.ReadString(), version = r.ReadInt64() };
    }

    /// <summary>What a game installation looks like; compared at join time (see <see cref="Compat"/>).</summary>
    public sealed class Manifest
    {
        public uint build;
        public string coopVersion = "";
        /// <summary>DlcManager.GetActiveDLCIds(): DLC/content packs switched on in this game (main-menu toggle + ownership).</summary>
        public List<string> dlcs = new List<string>();
        /// <summary>Enabled mods other than OniCoop itself.</summary>
        public List<ModEntry> mods = new List<ModEntry>();

        public void Write(BinaryWriter w)
        {
            w.Write(build);
            w.Write(coopVersion);
            w.WriteStrings(dlcs);
            w.Write((ushort)mods.Count);
            foreach (var m in mods) m.Write(w);
        }

        public static Manifest Read(BinaryReader r)
        {
            var m = new Manifest { build = r.ReadUInt32(), coopVersion = r.ReadString(), dlcs = r.ReadStrings() };
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++) m.mods.Add(ModEntry.Read(r));
            return m;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("build ").Append(build).Append(", coop ").Append(coopVersion);
            sb.Append(", dlc [").Append(string.Join(",", dlcs.ToArray())).Append("], mods [");
            for (int i = 0; i < mods.Count; i++)
                sb.Append(i > 0 ? "," : "").Append(mods[i].staticId).Append('@').Append(mods[i].version);
            return sb.Append(']').ToString();
        }
    }

    public sealed class HelloMsg
    {
        public ushort protocol = Proto.Version;
        public string token = "";
        public string room = "default";
        public string name = "";
        public Role role;
        /// <summary>Guest only: send me the host's world (false when reconnecting from inside a game).</summary>
        public bool wantWorld;
        public Manifest manifest = new Manifest();

        public byte[] Encode() => Wire.Build(w =>
        {
            w.Write(protocol); w.Write(token); w.Write(room); w.Write(name); w.Write((byte)role); w.Write(wantWorld);
            manifest.Write(w);
        });

        public static HelloMsg Decode(byte[] p)
        {
            var r = Wire.Reader(p);
            var h = new HelloMsg { protocol = r.ReadUInt16() };
            if (h.protocol != Proto.Version) return h; // the rest may be laid out differently
            h.token = r.ReadString(); h.room = r.ReadString(); h.name = r.ReadString();
            h.role = (Role)r.ReadByte(); h.wantWorld = r.ReadBoolean();
            h.manifest = Manifest.Read(r);
            return h;
        }
    }

    public sealed class WelcomeMsg
    {
        public ushort id;
        public ulong udpKey;
        public byte color;
        public string relayVersion = "";
        public List<string> warnings = new List<string>();

        public byte[] Encode() => Wire.Build(w => { w.Write(id); w.Write(udpKey); w.Write(color); w.Write(relayVersion); w.WriteStrings(warnings); });
        public static WelcomeMsg Decode(byte[] p)
        {
            var r = Wire.Reader(p);
            return new WelcomeMsg { id = r.ReadUInt16(), udpKey = r.ReadUInt64(), color = r.ReadByte(), relayVersion = r.ReadString(), warnings = r.ReadStrings() };
        }
    }

    public sealed class RejectMsg
    {
        public RejectCode code;
        public string message = "";
        public List<string> details = new List<string>();

        public byte[] Encode() => Wire.Build(w => { w.Write((byte)code); w.Write(message); w.WriteStrings(details); });
        public static RejectMsg Decode(byte[] p)
        {
            var r = Wire.Reader(p);
            return new RejectMsg { code = (RejectCode)r.ReadByte(), message = r.ReadString(), details = r.ReadStrings() };
        }
    }

    public sealed class MemberInfo
    {
        public ushort id;
        public string name = "";
        public Role role;
        public MemberState state;
        public byte color;
        public bool udp;

        public static byte[] EncodeList(IList<MemberInfo> list) => Wire.Build(w =>
        {
            w.Write((ushort)list.Count);
            foreach (var m in list) { w.Write(m.id); w.Write(m.name); w.Write((byte)m.role); w.Write((byte)m.state); w.Write(m.color); w.Write(m.udp); }
        });

        public static List<MemberInfo> DecodeList(byte[] p)
        {
            var r = Wire.Reader(p);
            int n = r.ReadUInt16();
            var list = new List<MemberInfo>(n);
            for (int i = 0; i < n; i++)
                list.Add(new MemberInfo { id = r.ReadUInt16(), name = r.ReadString(), role = (Role)r.ReadByte(), state = (MemberState)r.ReadByte(), color = r.ReadByte(), udp = r.ReadBoolean() });
            return list;
        }
    }

    /// <summary>C→S: where I am now. A host in game also says which DLC the loaded save uses.</summary>
    public sealed class StateMsg
    {
        public MemberState state;
        public List<string> gameDlcs = new List<string>();
        public string colony = "";

        public byte[] Encode() => Wire.Build(w => { w.Write((byte)state); w.WriteStrings(gameDlcs); w.Write(colony); });
        public static StateMsg Decode(byte[] p)
        {
            var r = Wire.Reader(p);
            return new StateMsg { state = (MemberState)r.ReadByte(), gameDlcs = r.ReadStrings(), colony = r.ReadString() };
        }
    }

    public sealed class SaveBeginMsg
    {
        public uint requestId;
        public string name = "";
        public long size;
        public byte[] sha256 = new byte[32];

        public byte[] Encode() => Wire.Build(w => { w.Write(requestId); w.Write(name); w.Write(size); w.Write(sha256); });
        public static SaveBeginMsg Decode(byte[] p)
        {
            var r = Wire.Reader(p);
            return new SaveBeginMsg { requestId = r.ReadUInt32(), name = r.ReadString(), size = r.ReadInt64(), sha256 = r.ReadBytes(32) };
        }
    }

    /// <summary>A player's camera and cursor, in world units (1 unit = 1 cell). Sent ~10 times a second.</summary>
    public sealed class PresenceData
    {
        public const byte FlagCursor = 1, FlagInGame = 2;

        public uint seq;
        /// <summary>ClusterManager.activeWorldId of the sender (always 0 in the base game).</summary>
        public int world;
        public float viewMinX, viewMinY, viewMaxX, viewMaxY;
        public float cursorX, cursorY;
        public byte flags;
        /// <summary>Sender's own round trip to the relay, for display.</summary>
        public ushort rttMs;

        public void Write(BinaryWriter w)
        {
            w.Write(seq); w.Write(world);
            w.Write(viewMinX); w.Write(viewMinY); w.Write(viewMaxX); w.Write(viewMaxY);
            w.Write(cursorX); w.Write(cursorY); w.Write(flags); w.Write(rttMs);
        }

        public static PresenceData Read(BinaryReader r) => new PresenceData
        {
            seq = r.ReadUInt32(), world = r.ReadInt32(),
            viewMinX = r.ReadSingle(), viewMinY = r.ReadSingle(), viewMaxX = r.ReadSingle(), viewMaxY = r.ReadSingle(),
            cursorX = r.ReadSingle(), cursorY = r.ReadSingle(), flags = r.ReadByte(), rttMs = r.ReadUInt16(),
        };

        public byte[] Encode() => Wire.Build(Write);
    }
}
