using System.Numerics;
using System.Text;
using AtlasWH3.Core.Rendering.Vfx;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Vfx;

namespace AtlasWH3.Tests;

/// <summary>VFX in the 3D view: the vfx/*.xml reader, effect resolution through references, and the particle simulation.</summary>
public class VfxTests
{
    private static string Key(float t, float v, string ease = "linear") => FormattableString.Invariant($"{t},{v},linear,{ease},-0.05,0.0,0.05,0.0,0");
    private static string Constant(float v) => $"x_min,0,0;{Key(0, v)}|x_max,0,0;{Key(0, v)}:x,0;0,1";

    [Fact]
    public void Curve_SortsKeysAndPicksBetweenItsChannels()
    {
        // The torch fire's alpha is written 0, 1, 0.25 in time.
        var alpha = VfxCurve.Parse($"a_min,1,1;{Key(0, 0)};{Key(1, 0)};{Key(0.25f, 1)}|a_max,1,1;{Key(0, 0)};{Key(1, 0)};{Key(0.25f, 1)}:alpha,0;0,1")!;
        Assert.Equal(0, alpha.Evaluate(0, 0), 5);
        Assert.Equal(1, alpha.Evaluate(0, 0.25f), 5);
        Assert.Equal(0.5f, alpha.Evaluate(0, 0.625f), 5);
        Assert.Equal(0, alpha.Evaluate(0, 2), 5);

        // random 1: between min and max; random 0: the min channel only.
        var speed = VfxCurve.Parse($"x_min,0,0;{Key(0, 0.15f)}|x_max,0,0;{Key(0, 0.6f)}:x,1;0,1")!;
        Assert.Equal(0.15f, speed.Evaluate(0, 0.5f, 0), 5);
        Assert.Equal(0.6f, speed.Evaluate(0, 0.5f, 1), 5);
        var fixedSpeed = VfxCurve.Parse($"x_min,0,0;{Key(0, 0.15f)}|x_max,0,0;{Key(0, 0.6f)}:x,0;0,1")!;
        Assert.Equal(0.15f, fixedSpeed.Evaluate(0, 0.5f, 1), 5);

        Assert.Equal(7, speed.Evaluate(3, 0, 0, fallback: 7));
        Assert.Null(VfxCurve.Parse("no colon here"));
        Assert.Null(VfxCurve.Parse("x,0,0;1,2,linear,linear,0,0,0,0,0:x,0;0,5"));
    }

    private const string Library = """
        <shader_library file="fx/particle_vfx_library.hlsl"/><vfx revision="2">
          <emitters>
            <emitter type="continuous_mesh" id="fire">
              <property name="EmissionRateCurve" quality="0">RATE</property>
              <property name="ParticleLifeTime" quality="0">LIFE</property>
              <property name="InfiniteLife" quality="0" value="true"/>
              <property name="Enable" quality="2" value="false"/>
            </emitter>
            <emitter type="continuous_mesh" id="smoke">
              <property name="EmissionRateCurve" quality="0">RATE</property>
              <property name="InfiniteLife" quality="0" value="true"/>
            </emitter>
            <emitter type="continuous_mesh" id="glow"/>
          </emitters>
          <behaviour_chains>
            <behaviour_chain id="fire">
              <quality value="0"><modifier fragment="ps_texture"><texture_property name="diffuse" type="file" path="vfx\textures\fire.dds"/></modifier></quality>
              <quality value="1"><modifier fragment="ps_texture"><texture_property name="diffuse" type="file" path="vfx\textures\fire_low.dds"/></modifier></quality>
            </behaviour_chain>
            <behaviour_chain id="smoke">
              <quality value="0"><modifier fragment="ps_texture"><texture_property name="diffuse" type="file" path="vfx\textures\smoke.dds"/></modifier></quality>
            </behaviour_chain>
          </behaviour_chains>
          <effect_render_stacks>
            <effect_render_stack id="glow"><property name="scene_view_type" quality="0" value="DEFERRED_DYNAMIC_LIGHT"/></effect_render_stack>
          </effect_render_stacks>
        </vfx>
        """;

    private const string Placed = """
        <vfx revision="2"><emitters></emitters></vfx><vfx_references>
          <vfx inst_name="xref" vfx_ref="lib" scale_min="0.5" scale_max="0.5" pos_x="0" pos_y="2" pos_z="0" rot_x="0" rot_y="0" rot_z="0"
               start_time_min="0" start_time_max="60" infinite_life="true" simulation_speed_multiplier="1">
            <emitter name="fire" enable="true" tint_min_a="0.5" tint_max_a="0.5"/>
            <emitter name="smoke" enable="false"/>
          </vfx>
          <vfx inst_name="loop" vfx_ref="placed"/>
        </vfx_references>
        """;

    private static byte[] Utf16(string text) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    private static VfxLibrary Fake() => new(path => path switch
    {
        "vfx/lib.xml" => Utf16(Library.Replace("RATE", Constant(20)).Replace("LIFE", Constant(0.5f))),
        "vfx/placed.xml" => Utf16(Placed),
        _ => null,
    });

    [Fact]
    public void Document_ReadsQualityZeroOnly()
    {
        var doc = VfxDocument.Parse(Utf16(Library.Replace("RATE", Constant(20)).Replace("LIFE", Constant(0.5f))));
        Assert.Equal(["fire", "smoke", "glow"], doc.Emitters.Select(e => e.Id));
        Assert.False(doc.Emitters[0].Properties.ContainsKey("Enable"));    // written at quality 2 only
        Assert.Equal("vfx\\textures\\fire.dds", doc.Chains["fire"][0].Textures["diffuse"]);
        Assert.Equal("DEFERRED_DYNAMIC_LIGHT", doc.SceneViews["glow"]);
    }

    [Fact]
    public void Resolve_PlaysOnlyTheEmittersAReferenceEnables()
    {
        var effect = Fake().Resolve("placed");
        var fire = Assert.Single(effect.Emitters);
        Assert.Equal("lib/fire", fire.Name);
        Assert.Equal("vfx/textures/fire.dds", fire.Texture);
        Assert.Equal(0.5f, fire.TintMin.W);
        Assert.Equal(new Vector2(0, 60), fire.StartTime);                  // drawn per placed instance
        Assert.Equal(new Vector3(0, 2, 0), fire.Local.Translation);
        Assert.Equal(0.5f, new Vector3(fire.Local.M11, fire.Local.M12, fire.Local.M13).Length(), 5);
        Assert.Contains(effect.Problems, p => p.Contains("cycle"));

        // Unlisted: every emitter plays; the light is kept but not drawn.
        var all = Fake().Resolve("lib");
        Assert.Equal(3, all.Emitters.Count);
        Assert.Equal(["lib/fire", "lib/smoke"], all.Drawn.Select(e => e.Name));
        Assert.Contains(all.Emitters.Single(e => e.Name == "lib/glow").Skipped, s => s.Contains("dynamic light"));
    }

    [Fact]
    public void Instance_SpawnsAtItsRateAndDrawsSixVerticesAParticle()
    {
        var effect = Fake().Resolve("lib");
        var instance = new VfxInstance(effect, Matrix4x4.CreateTranslation(10, 0, 0), 1);
        instance.Advance(2);
        // fire: 20/s living 0.5 s, about 10 alive; smoke: 20/s living 1 s (no life curve), about 20.
        Assert.InRange(instance.ParticleCount, 25, 35);
        var view = new VfxView(new Vector3(10, 1, -5), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var vertices = new List<ParticleVertex>();
        Assert.True(instance.Write(0, view, vertices));
        Assert.Equal(0, vertices.Count % 6);
        Assert.All(vertices, v => Assert.InRange(v.Position.X, 9, 11));

        // Bursts: 0.2 s of spawning every 60 s.
        var bursty = new VfxEmitterSpec
        {
            Name = "b", Infinite = true, Rate = VfxCurve.Constant(100), ParticleLife = VfxCurve.Constant(0.1f), Texture = "t.dds",
            Burst = (new Vector2(60, 60), new Vector2(0.2f, 0.2f)),
        };
        var strike = new VfxInstance(new VfxEffect("b", [bursty], []), Matrix4x4.Identity, 1);
        var peak = 0;
        for (var i = 0; i < 20; i++) { strike.Advance(0.05f); peak = Math.Max(peak, strike.ParticleCount); }
        Assert.InRange(peak, 5, 12);
        Assert.Equal(0, strike.ParticleCount);
    }

    [Fact]
    public void Fade_IsLinearBetweenTheCullDistances()
    {
        var cull = new Vector4(0.5f, 3, 30, 60);
        Assert.Equal(0, VfxInstance.Fade(0.2f, cull));
        Assert.Equal(0.5f, VfxInstance.Fade(1.75f, cull), 5);
        Assert.Equal(1, VfxInstance.Fade(10, cull));
        Assert.Equal(0.5f, VfxInstance.Fade(45, cull), 5);
        Assert.Equal(0, VfxInstance.Fade(61, cull));
    }

    [Fact]
    public void Packs_BrazierFireDrawsItsFourEmitters()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var source = AssetSource.ForGame(TestKits.Wh3GameData);
        var effect = new VfxLibrary(source.TryRead).Resolve("wh_main_campaign_brazier_fire");
        Assert.Empty(effect.Problems);
        Assert.Equal(["wh3_main_lib_enviro1/fire_embers", "wh_main_lib_enviro_torch/embers_small", "wh_main_lib_enviro_torch/fire", "wh_main_lib_enviro_torch/smoke"],
            effect.Drawn.Select(e => e.Name));
        Assert.All(effect.Drawn, e => Assert.True(source.Exists(e.Texture!), e.Texture));
        var fire = effect.Drawn.Single(e => e.Name.EndsWith("/fire"));
        Assert.Equal((8, 8, true), (fire.CellsX, fire.CellsY, fire.Animated));
        var instance = new VfxInstance(effect, Matrix4x4.Identity, 1);
        instance.Warm();
        Assert.True(instance.ParticleCount > 0);
    }
}
