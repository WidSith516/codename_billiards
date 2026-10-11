using System;
using System.Collections.Generic;
using Godot;

namespace Rewind.Game;

/// <summary>
/// Audio front end: a small pool of players, one per concurrent sound, driven by the SoundBank's
/// synthesised clips.
///
/// Registered as an autoload so both the front end and the play scene use one volume setting and one
/// pool, and so a sound started in a level is not cut off by a scene change.
///
/// HEADLESS SAFETY: if the audio server is unavailable (headless gate runs, some CI containers) every
/// call degrades to a no-op instead of throwing. Verified by running the whole gate, which boots this
/// autoload, with no audio device.
/// </summary>
public partial class Audio : Node
{
    private const int PoolSize = 12;

    private readonly List<AudioStreamPlayer> _pool = new();
    private int _next;

    private float _volumeDb = -6.0f;

    /// <summary>Whether audio is actually usable this run.</summary>
    public bool Available { get; private set; }

    /// <summary>Master volume 0..1, persisted by the front end.</summary>
    public float Volume
    {
        get => Mathf.DbToLinear(_volumeDb);
        set
        {
            _volumeDb = Mathf.LinearToDb(Mathf.Clamp(value, 0.0001f, 1.0f));
            ApplyVolume();
        }
    }

    /// <summary>Mute for captures, so a scripted run is silent regardless of settings.</summary>
    public bool Muted { get; set; }

    public override void _Ready()
    {
        try
        {
            // Touching the audio server is what fails on a machine with no audio; probe once.
            Available = AudioServer.GetBusCount() > 0;
        }
        catch (Exception)
        {
            Available = false;
        }

        if (!Available) return;

        try
        {
            for (var i = 0; i < PoolSize; i++)
            {
                var p = new AudioStreamPlayer { Bus = "Master" };
                AddChild(p);
                _pool.Add(p);
            }
        }
        catch (Exception)
        {
            Available = false;
        }

        // A muted flag from the command line keeps recorded footage silent on purpose.
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a == "--mute") Muted = true;
    }

    private void ApplyVolume()
    {
        foreach (var p in _pool) p.VolumeDb = _volumeDb;
    }

    /// <summary>
    /// Play a cue. <paramref name="pitch"/> shifts the clip, which the play scene uses to make
    /// repeated hits of the same kind sound different instead of machine-gunned.
    /// </summary>
    public void Play(SoundBank.Cue cue, float volumeScale = 1.0f, float pitch = 1.0f)
    {
        if (!Available || Muted || _pool.Count == 0) return;
        if (volumeScale <= 0.001f) return;

        try
        {
            var player = _pool[_next];
            _next = (_next + 1) % _pool.Count;
            player.Stream = SoundBank.Get(cue);
            player.PitchScale = Mathf.Clamp(pitch, 0.25f, 4.0f);
            player.VolumeDb = _volumeDb + Mathf.LinearToDb(Mathf.Clamp(volumeScale, 0.0001f, 1.0f));
            player.Play();
        }
        catch (Exception)
        {
            // An audio failure must never take the game down mid-level.
            Available = false;
        }
    }
}
