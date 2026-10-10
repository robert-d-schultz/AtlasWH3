using System.Xml.Linq;
using AtlasWH3.Core;
using AtlasWH3.Core.Bob;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Rivers;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Tests;

/// <summary>WH3's river bake (<see cref="Wh3River"/>) and the rivers step.</summary>
public class Wh3RiverTests
{
    private const string OldWorld = "cr_oldworld_map_1", Iee = "cr_combi_expanded_map_1";

    /// <summary>
    /// The whole step against the river models BOB wrote (2026-10-09): every model is identical apart from the two bytes
    /// BOB leaves uninitialised, and every .wsmodel is identical. The user's working_data: IEE all 266 rivers (the BOB
    /// reprocess of 2026-10-10; the older run had 60), Old World 127 (one with its first point at y -0.757, one reversed,
    /// 2310-row waterfalls). The scratch kit's fresh BOB runs: all 266 IEE rivers (15 with first points 1e-6 off in x/z)
    /// and Old World's 127.
    /// </summary>
    [Theory]
    [InlineData(Iee, 266, false)]
    [InlineData(OldWorld, 127, false)]
    [InlineData(Iee, 266, true)]
    [InlineData(OldWorld, 127, true)]
    public void Step_MatchesBob(string map, int expected, bool scratchKit)
    {
        var kit = scratchKit ? ScratchKit.DefaultRoot(TestKits.Wh3Kit) : TestKits.Kit(map);
        var bob = Path.Combine(kit, "working_data", "terrain", "campaigns", map, "models");
        if (!Directory.Exists(bob)) return;
        var target = Path.Combine(Path.GetTempPath(), $"atlaswh3_rivers_test_{map}_{scratchKit}");
        try
        {
            var ctx = new CampaignBuildContext(new ProjectPaths { MapName = map, AssemblyKitRoot = kit, GameDataDir = TestKits.Wh3GameData }, target);
            Assert.Empty(new RiversStep().CheckInputs(ctx));
            new RiversStep().Run(ctx);
            var models = Directory.GetFiles(bob, "river_*.wsmodel.rigid_model_v2");
            Assert.Equal(expected, models.Length);
            foreach (var file in models)
            {
                var ours = ctx.OutFile("models", Path.GetFileName(file));
                Assert.True(File.Exists(ours), Path.GetFileName(file));
                byte[] a = File.ReadAllBytes(file), b = File.ReadAllBytes(ours);
                Assert.Equal(a.Length, b.Length);
                a[0x31A] = a[0x31B] = 0; // uninitialised in BOB
                Assert.True(a.AsSpan().SequenceEqual(b), Path.GetFileName(file));
                var wsmodel = file[..^".rigid_model_v2".Length];
                Assert.Equal(File.ReadAllText(wsmodel), File.ReadAllText(ours[..^".rigid_model_v2".Length]));
            }
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    private static Wh3River.Spline Straight(float y0, float width, bool reverse = false) => Wh3River.Read(XElement.Parse(FormattableString.Invariant($"""
        <entity id="1"><ECRiver/>
          <ECRiverSpline terrain_relative="false" reverse_direction="{(reverse ? "true" : "false")}" material="m">
            <spline closed="false">
              <point position="0,{y0},0" tangent_in="0,0,0" tangent_out="0,0,0" width="{width}"/>
              <point position="2,{y0},0" tangent_in="0,0,0" tangent_out="0,0,0" width="{width}"/>
            </spline>
          </ECRiverSpline>
        </entity>
        """)))!;

    /// <summary>A straight 2-unit river with zero tangents: straight length 2, so 10 steps of 0.2; width 0.5 is under
    /// the 1.0 column step, so 2 columns. u = ±width/20, v = length/10 at the end.</summary>
    [Fact]
    public void Build_StraightRiver_RowsColumnsAndUv()
    {
        var model = Wh3River.Build(Straight(0, 0.5f))!;
        Assert.Equal(11 * 2, model.VertexCount);
        Assert.Equal(10 * 6, model.Indices.Length);
        Assert.Equal([0f, 0f, -0.25f, 2f, 0f, 0.25f], model.Bounds);
        var uv = Enumerable.Range(0, model.VertexCount)
            .Select(i => (U: BitConverter.ToSingle(model.Vertices, i * 48 + 16), V: BitConverter.ToSingle(model.Vertices, i * 48 + 20))).ToList();
        Assert.Equal(0.025f, uv.Max(p => p.U));
        Assert.Equal(-0.025f, uv.Min(p => p.U));
        Assert.Equal(0.2f, uv.Max(p => p.V), 6);
        // 2.5 wide: ceil(2.5 / 1.0) + 1 columns
        Assert.Equal(11 * 4, Wh3River.Build(Straight(0, 2.5f))!.VertexCount);
    }

    /// <summary>BOB takes the heights relative to the stored first point's, and reversing only changes the walk.</summary>
    [Fact]
    public void Build_HeightsFromTheFirstPoint_ReverseKeepsTheGeometry()
    {
        var raised = Straight(-0.75f, 0.5f);
        Assert.Equal(-0.75f, raised.BaseHeight);
        Assert.Equal(Wh3River.Build(Straight(0, 0.5f))!.Bounds, Wh3River.Build(raised)!.Bounds);
        Assert.Equal(Wh3River.Build(Straight(0, 0.5f))!.Bounds, Wh3River.Build(Straight(0, 0.5f, reverse: true))!.Bounds);
    }

    [Fact]
    public void Build_NoSegment_IsNull()
    {
        var single = Straight(0, 1) with { Points = [Straight(0, 1).Points[0]] };
        Assert.Null(Wh3River.Build(single));
    }

    [Fact]
    public void WsModel_River_IsBobsLayout()
    {
        Assert.Equal("<model version=\"1\">\n  <geometry>terrain/campaigns/m/models/river_1a.wsmodel.rigid_model_v2</geometry>\n"
                     + "  <materials>\n    <material lod_index=\"0\" part_index=\"0\">x.material</material>\n  </materials>\n</model>",
            WsModel.River("m", "1a", "x.material"));
    }
}
