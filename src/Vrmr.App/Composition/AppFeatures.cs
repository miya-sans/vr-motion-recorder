namespace VRMR.App.Composition
{
    /// <summary>Parts of the app that are in the tree but switched off until they are finished.</summary>
    public static class AppFeatures
    {
        /// <summary>
        /// The Scene workspace (arranging takes on tracks). Off until the scene editor is ported: the Takes | Scene
        /// switch is hidden and the app stays in the Takes workspace. Set to true to show the switch again.
        /// </summary>
        public static readonly bool SceneWorkspace = false;
    }
}
