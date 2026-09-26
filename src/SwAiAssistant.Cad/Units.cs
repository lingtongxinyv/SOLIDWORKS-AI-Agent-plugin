namespace SwAiAssistant.Cad
{
    /// <summary>
    /// 单位换算：SolidWorks COM API 内部统一使用米/千克（MKS），
    /// 面板、Planner 与特征树对外统一使用毫米（GB 工程惯例）。
    /// </summary>
    public static class Units
    {
        public const double MetersPerMm = 0.001;

        public static double MmToM(double mm) => mm * MetersPerMm;

        public static double MToMm(double m) => m / MetersPerMm;

        public static double Mm3ToM3(double mm3) => mm3 * 1e-9;

        public static double M3ToMm3(double m3) => m3 * 1e9;

        public static double DegToRad(double deg) => deg * System.Math.PI / 180.0;
    }
}
