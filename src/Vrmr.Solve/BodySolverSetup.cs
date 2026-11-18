namespace VRMR
{
    /// <summary>Registers the IK solver used for any-world body reconstruction.</summary>
    public static class BodySolverSetup
    {
        public static void Register()
        {
            BodySolvers.Factory = () => new FbikAdapter();
        }
    }
}
