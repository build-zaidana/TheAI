using System.Windows.Media;

namespace CodexBar.App;

// Icons from Lucide (https://lucide.dev, ISC licence, shipped as Licenses\Lucide-LICENSE.txt),
// converted from lucide-static 0.469.0: each is drawn on a 24x24 grid as a 2 px round-capped
// stroke, so a Path gives them Stretch="Uniform" and a stroke of about 1.6 at 16 px.
internal static class Icons
{
    public static readonly Geometry Sun = Freeze("M 8,12 A 4,4 0 1 1 16,12 A 4,4 0 1 1 8,12 Z M 12 2 v 2 M 12 20 v 2 M 4.93 4.93 l 1.41 1.41 M 17.66 17.66 l 1.41 1.41 M 2 12 h 2 M 20 12 h 2 M 6.34 17.66 l -1.41 1.41 M 19.07 4.93 l -1.41 1.41");
    public static readonly Geometry Moon = Freeze("M 12 3 a 6 6 0 0 0 9 9 9 9 0 1 1 -9 -9 Z");
    public static readonly Geometry Cloud = Freeze("M 17.5 19 H 9 a 7 7 0 1 1 6.71 -9 h 1.79 a 4.5 4.5 0 1 1 0 9 Z");
    public static readonly Geometry CloudSun = Freeze("M 12 2 v 2 M 4.93 4.93 l 1.41 1.41 M 20 12 h 2 M 19.07 4.93 l -1.41 1.41 M 15.947 12.65 a 4 4 0 0 0 -5.925 -4.128 M 13 22 H 7 a 5 5 0 1 1 4.9 -6 H 13 a 3 3 0 0 1 0 6 Z");
    public static readonly Geometry CloudMoon = Freeze("M 10.188 8.5 A 6 6 0 0 1 16 4 a 1 1 0 0 0 6 6 6 6 0 0 1 -3 5.197 M 13 16 a 3 3 0 1 1 0 6 H 7 a 5 5 0 1 1 4.9 -6 Z");
    public static readonly Geometry CloudRain = Freeze("M 4 14.899 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.5 8.242 M 16 14 v 6 M 8 14 v 6 M 12 16 v 6");
    public static readonly Geometry CloudDrizzle = Freeze("M 4 14.899 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.5 8.242 M 8 19 v 1 M 8 14 v 1 M 16 19 v 1 M 16 14 v 1 M 12 21 v 1 M 12 16 v 1");
    public static readonly Geometry CloudSnow = Freeze("M 4 14.899 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.5 8.242 M 8 15 h .01 M 8 19 h .01 M 12 17 h .01 M 12 21 h .01 M 16 15 h .01 M 16 19 h .01");
    public static readonly Geometry CloudLightning = Freeze("M 6 16.326 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 .5 8.973 M 13 12 l -3 5 h 4 l -3 5");
    public static readonly Geometry CloudFog = Freeze("M 4 14.899 A 7 7 0 1 1 15.71 8 h 1.79 a 4.5 4.5 0 0 1 2.5 8.242 M 16 17 H 7 M 17 21 H 9");
    public static readonly Geometry Droplet = Freeze("M 12 22 a 7 7 0 0 0 7 -7 c 0 -2 -1 -3.9 -3 -5.5 s -3.5 -4 -4 -6.5 c -.5 2.5 -2 4.9 -4 6.5 C 6 11.1 5 13 5 15 a 7 7 0 0 0 7 7 z");
    public static readonly Geometry Bell = Freeze("M 10.268 21 a 2 2 0 0 0 3.464 0 M 3.262 15.326 A 1 1 0 0 0 4 17 h 16 a 1 1 0 0 0 .74 -1.673 C 19.41 13.956 18 12.499 18 8 A 6 6 0 0 0 6 8 c 0 4.499 -1.411 5.956 -2.738 7.326");
    public static readonly Geometry BellRing = Freeze("M 10.268 21 a 2 2 0 0 0 3.464 0 M 22 8 c 0 -2.3 -.8 -4.3 -2 -6 M 3.262 15.326 A 1 1 0 0 0 4 17 h 16 a 1 1 0 0 0 .74 -1.673 C 19.41 13.956 18 12.499 18 8 A 6 6 0 0 0 6 8 c 0 4.499 -1.411 5.956 -2.738 7.326 M 4 2 C 2.8 3.7 2 5.7 2 8");
    public static readonly Geometry Calendar = Freeze("M 8 2 v 4 M 16 2 v 4 M 5,4 H 19 A 2,2 0 0 1 21,6 V 20 A 2,2 0 0 1 19,22 H 5 A 2,2 0 0 1 3,20 V 6 A 2,2 0 0 1 5,4 Z M 3 10 h 18");
    public static readonly Geometry Mic = Freeze("M 12 2 a 3 3 0 0 0 -3 3 v 7 a 3 3 0 0 0 6 0 V 5 a 3 3 0 0 0 -3 -3 Z M 19 10 v 2 a 7 7 0 0 1 -14 0 v -2 M 12,19 L 12,22");
    public static readonly Geometry MicOff = Freeze("M 2,2 L 22,22 M 18.89 13.23 A 7.12 7.12 0 0 0 19 12 v -2 M 5 10 v 2 a 7 7 0 0 0 12 5 M 15 9.34 V 5 a 3 3 0 0 0 -5.68 -1.33 M 9 9 v 3 a 3 3 0 0 0 5.12 2.12 M 12,19 L 12,22");
    public static readonly Geometry Video = Freeze("M 16 13 l 5.223 3.482 a .5 .5 0 0 0 .777 -.416 V 7.87 a .5 .5 0 0 0 -.752 -.432 L 16 10.5 M 4,6 H 14 A 2,2 0 0 1 16,8 V 16 A 2,2 0 0 1 14,18 H 4 A 2,2 0 0 1 2,16 V 8 A 2,2 0 0 1 4,6 Z");
    public static readonly Geometry Keyboard = Freeze("M 10 8 h .01 M 12 12 h .01 M 14 8 h .01 M 16 12 h .01 M 18 8 h .01 M 6 8 h .01 M 7 16 h 10 M 8 12 h .01 M 4,4 H 20 A 2,2 0 0 1 22,6 V 18 A 2,2 0 0 1 20,20 H 4 A 2,2 0 0 1 2,18 V 6 A 2,2 0 0 1 4,4 Z");
    public static readonly Geometry Wifi = Freeze("M 12 20 h .01 M 2 8.82 a 15 15 0 0 1 20 0 M 5 12.859 a 10 10 0 0 1 14 0 M 8.5 16.429 a 5 5 0 0 1 7 0");
    public static readonly Geometry WifiOff = Freeze("M 12 20 h .01 M 8.5 16.429 a 5 5 0 0 1 7 0 M 5 12.859 a 10 10 0 0 1 5.17 -2.69 M 19 12.859 a 10 10 0 0 0 -2.007 -1.523 M 2 8.82 a 15 15 0 0 1 4.177 -2.643 M 22 8.82 a 15 15 0 0 0 -11.288 -3.764 M 2 2 l 20 20");
    public static readonly Geometry CapsLock = Freeze("M 9 19 h 6 M 9 15 v -3 H 5 l 7 -7 7 7 h -4 v 3 H 9 z");
    public static readonly Geometry MoonStar = Freeze("M 12 3 a 6 6 0 0 0 9 9 9 9 0 1 1 -9 -9 M 20 3 v 4 M 22 5 h -4");
    public static readonly Geometry Clock = Freeze("M 2,12 A 10,10 0 1 1 22,12 A 10,10 0 1 1 2,12 Z M 12,6 L 12,12 L 16,14");
    public static readonly Geometry Brightness = Freeze("M 8,12 A 4,4 0 1 1 16,12 A 4,4 0 1 1 8,12 Z M 12 3 v 1 M 12 20 v 1 M 3 12 h 1 M 20 12 h 1 M 18.364 5.636 l -.707 .707 M 6.343 17.657 l -.707 .707 M 5.636 5.636 l .707 .707 M 17.657 17.657 l .707 .707");
    public static readonly Geometry Volume = Freeze("M 11 4.702 a .705 .705 0 0 0 -1.203 -.498 L 6.413 7.587 A 1.4 1.4 0 0 1 5.416 8 H 3 a 1 1 0 0 0 -1 1 v 6 a 1 1 0 0 0 1 1 h 2.416 a 1.4 1.4 0 0 1 .997 .413 l 3.383 3.384 A .705 .705 0 0 0 11 19.298 z M 16 9 a 5 5 0 0 1 0 6 M 19.364 18.364 a 9 9 0 0 0 0 -12.728");
    public static readonly Geometry VolumeMute = Freeze("M 11 4.702 a .705 .705 0 0 0 -1.203 -.498 L 6.413 7.587 A 1.4 1.4 0 0 1 5.416 8 H 3 a 1 1 0 0 0 -1 1 v 6 a 1 1 0 0 0 1 1 h 2.416 a 1.4 1.4 0 0 1 .997 .413 l 3.383 3.384 A .705 .705 0 0 0 11 19.298 z M 22,9 L 16,15 M 16,9 L 22,15");
    public static readonly Geometry BatteryLow = Freeze("M 4,7 H 16 A 2,2 0 0 1 18,9 V 15 A 2,2 0 0 1 16,17 H 4 A 2,2 0 0 1 2,15 V 9 A 2,2 0 0 1 4,7 Z M 22,11 L 22,13 M 6,11 L 6,13");
    public static readonly Geometry BatteryCharging = Freeze("M 15 7 h 1 a 2 2 0 0 1 2 2 v 6 a 2 2 0 0 1 -2 2 h -2 M 6 7 H 4 a 2 2 0 0 0 -2 2 v 6 a 2 2 0 0 0 2 2 h 1 M 11 7 l -3 5 h 4 l -3 5 M 22,11 L 22,13");
    public static readonly Geometry Headphones = Freeze("M 3 14 h 3 a 2 2 0 0 1 2 2 v 3 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 v -7 a 9 9 0 0 1 18 0 v 7 a 2 2 0 0 1 -2 2 h -1 a 2 2 0 0 1 -2 -2 v -3 a 2 2 0 0 1 2 -2 h 3");

    private static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
