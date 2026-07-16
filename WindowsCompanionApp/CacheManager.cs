namespace VSDM18App
{
    public class CacheManager
    {
        private class Color
        {
            public int R { get; set; }
            public int G { get; set; }
            public int B { get; set; }
        }

        private class Save
        {
            public Color leftLEDColor { get; set; }
            public Color rightLEDColor { get; set; }
            public Color ringLEDColor { get; set; }
        }
    }
}