using Best.SocketIO.Examples.Helpers.SelectorUI;

using UnityEngine;

namespace Best.SocketIO.Examples.Helpers
{
    abstract class SampleBase : MonoBehaviour
    {
#pragma warning disable 0649, 0169
        [Header("Common Properties")]
        public string Category;
        public string DisplayName;

        [TextArea]
        public string Description;
#pragma warning restore

        public RuntimePlatform[] BannedPlatforms = new RuntimePlatform[0];

        protected SampleSelectorUI sampleSelector;

        protected virtual void Start()
        {
            this.sampleSelector = FindAnyObjectByType<SampleSelectorUI>();
        }
    }
}
