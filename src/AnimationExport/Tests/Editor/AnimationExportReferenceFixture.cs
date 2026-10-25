using System;
using UnityEngine;

namespace CharacterAnimationExport.Tests
{
    [Serializable] public sealed class FixtureEvents { public float[] _NormalizedTimes = { 0.3f }; }
    [Serializable] public sealed class FixtureTransition
    {
        public AnimationClip _Clip;
        public float _FadeDuration = 0.37f;
        public float _Speed = 0.8f;
        public FixtureEvents _Events = new FixtureEvents();
    }
    public sealed class AnimationExportReferenceFixture : ScriptableObject
    {
        public FixtureTransition[] animations;
        public string untouched = "keep this value";
    }
}
