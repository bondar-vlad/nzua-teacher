namespace NzuaTeacher.Core;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "nzua-teacher");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DbPath => Path.Combine(DataDir, "teacher.db");

    public static string AssetsDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "assets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
