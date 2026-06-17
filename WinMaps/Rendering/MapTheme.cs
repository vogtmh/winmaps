using System.Collections.Generic;
using Windows.UI;

namespace WinMaps.Rendering
{
    internal class MapTheme
    {
        public string Id;
        public string Name;
        public Color Background;
        public Color GpsAccuracyFill;
        public Color GpsAccuracyStroke;
        public Color GpsDotFill;
        public Color GpsDotHalo;

        public Dictionary<string, Color> RoadColors;
        public Dictionary<string, Color> RoadOutlineColors;
        public Dictionary<string, Color> ParkColors;
        public Color WaterColor;
        public Color DefaultRoadColor;
        public Color DefaultRoadOutlineColor;
        public Color DefaultParkColor;
        public Color PoiDotColor;
        public Color PoiHaloColor;
        public Color PoiTextColor;
        public Dictionary<string, Color> PoiColors;
        public Color BuildingFill;
        public Color BuildingStroke;
        public Color BuildingLabelColor;

        public Color GetPoiColor(string type)
        {
            if (PoiColors != null && PoiColors.TryGetValue(type, out var c))
                return c;
            return PoiDotColor;
        }

        public Color GetRoadColor(string subType)
        {
            if (RoadColors != null && RoadColors.TryGetValue(subType, out var c))
                return c;
            return DefaultRoadColor;
        }

        public Color GetRoadOutlineColor(string subType)
        {
            if (RoadOutlineColors != null && RoadOutlineColors.TryGetValue(subType, out var c))
                return c;
            return DefaultRoadOutlineColor;
        }

        public Color GetParkColor(string subType)
        {
            if (ParkColors != null && ParkColors.TryGetValue(subType, out var c))
                return c;
            return DefaultParkColor;
        }

        // Decode building subtype encoded as "name\x1fhint"
        public static (string name, string hint) DecodeBuildingSubType(string subType)
        {
            if (subType == null) return ("", "");
            int sep = subType.IndexOf('\x1f');
            if (sep < 0) return (subType, "");
            return (subType.Substring(0, sep), subType.Substring(sep + 1));
        }

        // ---- Built-in themes ----

        public static readonly MapTheme Light = new MapTheme
        {
            Id = "light",
            Name = "Light",
            Background = Color.FromArgb(255, 242, 239, 233),
            GpsAccuracyFill = Color.FromArgb(40, 0, 120, 255),
            GpsAccuracyStroke = Color.FromArgb(80, 0, 120, 255),
            GpsDotFill = Color.FromArgb(255, 0, 120, 255),
            GpsDotHalo = Colors.White,
            WaterColor = Color.FromArgb(255, 170, 211, 223),
            DefaultRoadColor = Color.FromArgb(255, 200, 200, 200),
            DefaultRoadOutlineColor = Color.FromArgb(255, 190, 190, 190),
            DefaultParkColor = Color.FromArgb(255, 195, 225, 178),
            PoiDotColor = Color.FromArgb(255, 180, 60, 60),
            PoiHaloColor = Colors.White,
            PoiTextColor = Color.FromArgb(255, 80, 40, 40),
            BuildingFill = Color.FromArgb(255, 217, 208, 201),
            BuildingStroke = Color.FromArgb(255, 195, 183, 173),
            BuildingLabelColor = Color.FromArgb(255, 100, 90, 80),
            PoiColors = new Dictionary<string, Color>
            {
                ["amenity"] = Color.FromArgb(255, 180, 60, 60),
                ["shop"] = Color.FromArgb(255, 172, 121, 44),
                ["tourism"] = Color.FromArgb(255, 0, 146, 115),
                ["healthcare"] = Color.FromArgb(255, 200, 40, 40),
                ["office"] = Color.FromArgb(255, 100, 100, 160),
            },
            RoadColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 233, 144, 160),
                ["motorway_link"] = Color.FromArgb(255, 233, 144, 160),
                ["trunk"] = Color.FromArgb(255, 249, 178, 156),
                ["trunk_link"] = Color.FromArgb(255, 249, 178, 156),
                ["primary"] = Color.FromArgb(255, 252, 214, 164),
                ["primary_link"] = Color.FromArgb(255, 252, 214, 164),
                ["secondary"] = Color.FromArgb(255, 246, 250, 187),
                ["secondary_link"] = Color.FromArgb(255, 246, 250, 187),
                ["tertiary"] = Colors.White,
                ["tertiary_link"] = Colors.White,
                ["residential"] = Colors.White,
                ["living_street"] = Colors.White,
                ["unclassified"] = Colors.White,
                ["service"] = Colors.White,
                ["pedestrian"] = Color.FromArgb(255, 221, 221, 238),
                ["footway"] = Color.FromArgb(255, 250, 128, 114),
                ["path"] = Color.FromArgb(255, 250, 128, 114),
                ["cycleway"] = Color.FromArgb(255, 0, 68, 204),
                ["track"] = Color.FromArgb(255, 177, 140, 75),
            },
            RoadOutlineColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 196, 80, 108),
                ["motorway_link"] = Color.FromArgb(255, 196, 80, 108),
                ["trunk"] = Color.FromArgb(255, 200, 130, 100),
                ["trunk_link"] = Color.FromArgb(255, 200, 130, 100),
                ["primary"] = Color.FromArgb(255, 200, 170, 110),
                ["primary_link"] = Color.FromArgb(255, 200, 170, 110),
            },
            ParkColors = new Dictionary<string, Color>
            {
                ["forest"] = Color.FromArgb(255, 157, 202, 138),
                ["wood"] = Color.FromArgb(255, 157, 202, 138),
                ["scrub"] = Color.FromArgb(255, 181, 211, 164),
                ["grass"] = Color.FromArgb(255, 205, 235, 176),
                ["meadow"] = Color.FromArgb(255, 205, 235, 176),
                ["farmland"] = Color.FromArgb(255, 237, 240, 214),
                ["orchard"] = Color.FromArgb(255, 172, 225, 161),
                ["vineyard"] = Color.FromArgb(255, 172, 225, 161),
                ["recreation_ground"] = Color.FromArgb(255, 223, 252, 226),
                ["park"] = Color.FromArgb(255, 200, 250, 204),
                ["garden"] = Color.FromArgb(255, 200, 250, 204),
                ["nature_reserve"] = Color.FromArgb(255, 178, 223, 162),
            }
        };

        public static readonly MapTheme Dark = new MapTheme
        {
            Id = "dark",
            Name = "Dark",
            Background = Color.FromArgb(255, 28, 28, 32),
            GpsAccuracyFill = Color.FromArgb(35, 60, 160, 255),
            GpsAccuracyStroke = Color.FromArgb(70, 60, 160, 255),
            GpsDotFill = Color.FromArgb(255, 60, 160, 255),
            GpsDotHalo = Color.FromArgb(255, 40, 40, 46),
            WaterColor = Color.FromArgb(255, 30, 60, 90),
            DefaultRoadColor = Color.FromArgb(255, 60, 60, 66),
            DefaultRoadOutlineColor = Color.FromArgb(255, 45, 45, 50),
            DefaultParkColor = Color.FromArgb(255, 30, 50, 35),
            PoiDotColor = Color.FromArgb(255, 200, 120, 100),
            PoiHaloColor = Color.FromArgb(255, 28, 28, 32),
            PoiTextColor = Color.FromArgb(255, 210, 180, 170),
            BuildingFill = Color.FromArgb(255, 45, 45, 52),
            BuildingStroke = Color.FromArgb(255, 65, 65, 75),
            BuildingLabelColor = Color.FromArgb(255, 160, 150, 140),
            PoiColors = new Dictionary<string, Color>
            {
                ["amenity"] = Color.FromArgb(255, 200, 120, 100),
                ["shop"] = Color.FromArgb(255, 220, 180, 80),
                ["tourism"] = Color.FromArgb(255, 80, 200, 170),
                ["healthcare"] = Color.FromArgb(255, 230, 90, 90),
                ["office"] = Color.FromArgb(255, 140, 140, 200),
            },
            RoadColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 140, 60, 80),
                ["motorway_link"] = Color.FromArgb(255, 140, 60, 80),
                ["trunk"] = Color.FromArgb(255, 150, 90, 60),
                ["trunk_link"] = Color.FromArgb(255, 150, 90, 60),
                ["primary"] = Color.FromArgb(255, 140, 120, 70),
                ["primary_link"] = Color.FromArgb(255, 140, 120, 70),
                ["secondary"] = Color.FromArgb(255, 90, 90, 70),
                ["secondary_link"] = Color.FromArgb(255, 90, 90, 70),
                ["tertiary"] = Color.FromArgb(255, 70, 70, 76),
                ["tertiary_link"] = Color.FromArgb(255, 70, 70, 76),
                ["residential"] = Color.FromArgb(255, 65, 65, 72),
                ["living_street"] = Color.FromArgb(255, 65, 65, 72),
                ["unclassified"] = Color.FromArgb(255, 65, 65, 72),
                ["service"] = Color.FromArgb(255, 55, 55, 60),
                ["pedestrian"] = Color.FromArgb(255, 60, 55, 75),
                ["footway"] = Color.FromArgb(255, 120, 60, 55),
                ["path"] = Color.FromArgb(255, 120, 60, 55),
                ["cycleway"] = Color.FromArgb(255, 40, 70, 130),
                ["track"] = Color.FromArgb(255, 90, 75, 45),
            },
            RoadOutlineColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 100, 35, 55),
                ["motorway_link"] = Color.FromArgb(255, 100, 35, 55),
                ["trunk"] = Color.FromArgb(255, 110, 60, 40),
                ["trunk_link"] = Color.FromArgb(255, 110, 60, 40),
                ["primary"] = Color.FromArgb(255, 100, 85, 50),
                ["primary_link"] = Color.FromArgb(255, 100, 85, 50),
            },
            ParkColors = new Dictionary<string, Color>
            {
                ["forest"] = Color.FromArgb(255, 20, 50, 25),
                ["wood"] = Color.FromArgb(255, 20, 50, 25),
                ["scrub"] = Color.FromArgb(255, 28, 54, 30),
                ["grass"] = Color.FromArgb(255, 30, 52, 32),
                ["meadow"] = Color.FromArgb(255, 30, 52, 32),
                ["farmland"] = Color.FromArgb(255, 38, 44, 28),
                ["orchard"] = Color.FromArgb(255, 25, 52, 28),
                ["vineyard"] = Color.FromArgb(255, 25, 52, 28),
                ["recreation_ground"] = Color.FromArgb(255, 28, 58, 35),
                ["park"] = Color.FromArgb(255, 28, 58, 35),
                ["garden"] = Color.FromArgb(255, 28, 58, 35),
                ["nature_reserve"] = Color.FromArgb(255, 22, 55, 28),
            }
        };

        public static readonly MapTheme GMaps = new MapTheme
        {
            Id = "gmaps",
            Name = "gMaps",
            Background = Color.FromArgb(255, 229, 227, 223),
            GpsAccuracyFill = Color.FromArgb(40, 66, 133, 244),
            GpsAccuracyStroke = Color.FromArgb(80, 66, 133, 244),
            GpsDotFill = Color.FromArgb(255, 66, 133, 244),
            GpsDotHalo = Colors.White,
            WaterColor = Color.FromArgb(255, 163, 204, 255),
            DefaultRoadColor = Color.FromArgb(255, 200, 200, 200),
            DefaultRoadOutlineColor = Color.FromArgb(255, 180, 180, 180),
            DefaultParkColor = Color.FromArgb(255, 189, 215, 165),
            PoiDotColor = Color.FromArgb(255, 214, 72, 49),
            PoiHaloColor = Colors.White,
            PoiTextColor = Color.FromArgb(255, 100, 50, 40),
            BuildingFill = Color.FromArgb(255, 210, 207, 200),
            BuildingStroke = Color.FromArgb(255, 180, 176, 168),
            BuildingLabelColor = Color.FromArgb(255, 100, 90, 80),
            PoiColors = new Dictionary<string, Color>
            {
                ["amenity"] = Color.FromArgb(255, 214, 72, 49),
                ["shop"] = Color.FromArgb(255, 190, 145, 40),
                ["tourism"] = Color.FromArgb(255, 16, 163, 127),
                ["healthcare"] = Color.FromArgb(255, 220, 50, 50),
                ["office"] = Color.FromArgb(255, 110, 110, 170),
            },
            RoadColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 255, 183, 77),
                ["motorway_link"] = Color.FromArgb(255, 255, 183, 77),
                ["trunk"] = Color.FromArgb(255, 255, 198, 93),
                ["trunk_link"] = Color.FromArgb(255, 255, 198, 93),
                ["primary"] = Color.FromArgb(255, 255, 255, 255),
                ["primary_link"] = Color.FromArgb(255, 255, 255, 255),
                ["secondary"] = Color.FromArgb(255, 255, 255, 255),
                ["secondary_link"] = Color.FromArgb(255, 255, 255, 255),
                ["tertiary"] = Color.FromArgb(255, 255, 255, 255),
                ["tertiary_link"] = Color.FromArgb(255, 255, 255, 255),
                ["residential"] = Color.FromArgb(255, 255, 255, 255),
                ["living_street"] = Color.FromArgb(255, 255, 255, 255),
                ["unclassified"] = Color.FromArgb(255, 255, 255, 255),
                ["service"] = Color.FromArgb(255, 255, 255, 255),
                ["pedestrian"] = Color.FromArgb(255, 235, 235, 245),
                ["footway"] = Color.FromArgb(255, 190, 190, 190),
                ["path"] = Color.FromArgb(255, 190, 190, 190),
                ["cycleway"] = Color.FromArgb(255, 50, 120, 200),
                ["track"] = Color.FromArgb(255, 190, 180, 160),
            },
            RoadOutlineColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 224, 150, 40),
                ["motorway_link"] = Color.FromArgb(255, 224, 150, 40),
                ["trunk"] = Color.FromArgb(255, 224, 165, 50),
                ["trunk_link"] = Color.FromArgb(255, 224, 165, 50),
                ["primary"] = Color.FromArgb(255, 200, 200, 200),
                ["primary_link"] = Color.FromArgb(255, 200, 200, 200),
            },
            ParkColors = new Dictionary<string, Color>
            {
                ["forest"] = Color.FromArgb(255, 148, 195, 128),
                ["wood"] = Color.FromArgb(255, 148, 195, 128),
                ["scrub"] = Color.FromArgb(255, 170, 210, 150),
                ["grass"] = Color.FromArgb(255, 195, 225, 175),
                ["meadow"] = Color.FromArgb(255, 195, 225, 175),
                ["farmland"] = Color.FromArgb(255, 220, 230, 195),
                ["orchard"] = Color.FromArgb(255, 165, 215, 150),
                ["vineyard"] = Color.FromArgb(255, 165, 215, 150),
                ["recreation_ground"] = Color.FromArgb(255, 195, 230, 180),
                ["park"] = Color.FromArgb(255, 185, 220, 165),
                ["garden"] = Color.FromArgb(255, 185, 220, 165),
                ["nature_reserve"] = Color.FromArgb(255, 155, 210, 140),
            }
        };

        public static readonly MapTheme Neon = new MapTheme
        {
            Id = "neon",
            Name = "Neon",
            Background = Color.FromArgb(255, 10, 10, 18),
            GpsAccuracyFill = Color.FromArgb(30, 0, 255, 200),
            GpsAccuracyStroke = Color.FromArgb(60, 0, 255, 200),
            GpsDotFill = Color.FromArgb(255, 0, 255, 200),
            GpsDotHalo = Color.FromArgb(255, 15, 15, 25),
            WaterColor = Color.FromArgb(255, 10, 30, 60),
            DefaultRoadColor = Color.FromArgb(255, 40, 40, 55),
            DefaultRoadOutlineColor = Color.FromArgb(255, 25, 25, 35),
            DefaultParkColor = Color.FromArgb(255, 10, 30, 20),
            PoiDotColor = Color.FromArgb(255, 255, 0, 200),
            PoiHaloColor = Color.FromArgb(255, 10, 10, 18),
            PoiColors = new Dictionary<string, Color>
            {
                ["amenity"] = Color.FromArgb(255, 255, 0, 200),
                ["shop"] = Color.FromArgb(255, 255, 220, 0),
                ["tourism"] = Color.FromArgb(255, 0, 255, 180),
                ["healthcare"] = Color.FromArgb(255, 255, 50, 50),
                ["office"] = Color.FromArgb(255, 120, 100, 255),
            },
            PoiTextColor = Color.FromArgb(255, 200, 100, 255),
            BuildingFill = Color.FromArgb(255, 18, 18, 30),
            BuildingStroke = Color.FromArgb(255, 60, 60, 100),
            BuildingLabelColor = Color.FromArgb(255, 100, 100, 200),
            RoadColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 255, 0, 100),
                ["motorway_link"] = Color.FromArgb(255, 255, 0, 100),
                ["trunk"] = Color.FromArgb(255, 255, 80, 0),
                ["trunk_link"] = Color.FromArgb(255, 255, 80, 0),
                ["primary"] = Color.FromArgb(255, 255, 200, 0),
                ["primary_link"] = Color.FromArgb(255, 255, 200, 0),
                ["secondary"] = Color.FromArgb(255, 0, 200, 255),
                ["secondary_link"] = Color.FromArgb(255, 0, 200, 255),
                ["tertiary"] = Color.FromArgb(255, 50, 50, 80),
                ["tertiary_link"] = Color.FromArgb(255, 50, 50, 80),
                ["residential"] = Color.FromArgb(255, 45, 45, 70),
                ["living_street"] = Color.FromArgb(255, 45, 45, 70),
                ["unclassified"] = Color.FromArgb(255, 45, 45, 70),
                ["service"] = Color.FromArgb(255, 35, 35, 55),
                ["pedestrian"] = Color.FromArgb(255, 60, 30, 80),
                ["footway"] = Color.FromArgb(255, 150, 0, 255),
                ["path"] = Color.FromArgb(255, 150, 0, 255),
                ["cycleway"] = Color.FromArgb(255, 0, 255, 100),
                ["track"] = Color.FromArgb(255, 80, 60, 20),
            },
            RoadOutlineColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(120, 255, 0, 100),
                ["motorway_link"] = Color.FromArgb(120, 255, 0, 100),
                ["trunk"] = Color.FromArgb(120, 255, 80, 0),
                ["trunk_link"] = Color.FromArgb(120, 255, 80, 0),
                ["primary"] = Color.FromArgb(120, 255, 200, 0),
                ["primary_link"] = Color.FromArgb(120, 255, 200, 0),
            },
            ParkColors = new Dictionary<string, Color>
            {
                ["forest"] = Color.FromArgb(255, 3, 30, 12),
                ["wood"] = Color.FromArgb(255, 3, 30, 12),
                ["scrub"] = Color.FromArgb(255, 6, 34, 16),
                ["grass"] = Color.FromArgb(255, 8, 32, 18),
                ["meadow"] = Color.FromArgb(255, 8, 32, 18),
                ["farmland"] = Color.FromArgb(255, 14, 24, 12),
                ["orchard"] = Color.FromArgb(255, 4, 35, 16),
                ["vineyard"] = Color.FromArgb(255, 4, 35, 16),
                ["recreation_ground"] = Color.FromArgb(255, 5, 40, 20),
                ["park"] = Color.FromArgb(255, 5, 40, 20),
                ["garden"] = Color.FromArgb(255, 5, 40, 20),
                ["nature_reserve"] = Color.FromArgb(255, 3, 38, 15),
            }
        };

        public static readonly MapTheme Osm = new MapTheme
        {
            Id = "osm",
            Name = "OSM",
            Background = Color.FromArgb(255, 242, 239, 233),
            GpsAccuracyFill = Color.FromArgb(40, 0, 120, 255),
            GpsAccuracyStroke = Color.FromArgb(80, 0, 120, 255),
            GpsDotFill = Color.FromArgb(255, 0, 120, 255),
            GpsDotHalo = Colors.White,
            WaterColor = Color.FromArgb(255, 170, 211, 223),
            DefaultRoadColor = Color.FromArgb(255, 200, 200, 200),
            DefaultRoadOutlineColor = Color.FromArgb(255, 180, 180, 180),
            DefaultParkColor = Color.FromArgb(255, 195, 225, 178),
            PoiDotColor = Color.FromArgb(255, 180, 60, 60),
            PoiHaloColor = Colors.White,
            PoiTextColor = Color.FromArgb(255, 80, 40, 40),
            BuildingFill = Color.FromArgb(255, 217, 208, 201),
            BuildingStroke = Color.FromArgb(255, 196, 182, 171),
            BuildingLabelColor = Color.FromArgb(255, 100, 90, 80),
            PoiColors = new Dictionary<string, Color>
            {
                ["amenity"] = Color.FromArgb(255, 180, 60, 60),
                ["shop"] = Color.FromArgb(255, 172, 121, 44),
                ["tourism"] = Color.FromArgb(255, 0, 146, 115),
                ["healthcare"] = Color.FromArgb(255, 200, 40, 40),
                ["office"] = Color.FromArgb(255, 100, 100, 160),
            },
            RoadColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 232, 146, 162),
                ["motorway_link"] = Color.FromArgb(255, 232, 146, 162),
                ["trunk"] = Color.FromArgb(255, 249, 178, 156),
                ["trunk_link"] = Color.FromArgb(255, 249, 178, 156),
                ["primary"] = Color.FromArgb(255, 252, 214, 164),
                ["primary_link"] = Color.FromArgb(255, 252, 214, 164),
                ["secondary"] = Color.FromArgb(255, 243, 246, 182),
                ["secondary_link"] = Color.FromArgb(255, 243, 246, 182),
                ["tertiary"] = Colors.White,
                ["tertiary_link"] = Colors.White,
                ["residential"] = Colors.White,
                ["living_street"] = Colors.White,
                ["unclassified"] = Colors.White,
                ["service"] = Colors.White,
                ["pedestrian"] = Color.FromArgb(255, 218, 218, 235),
                ["footway"] = Color.FromArgb(255, 250, 128, 114),
                ["path"] = Color.FromArgb(255, 250, 128, 114),
                ["cycleway"] = Color.FromArgb(255, 0, 68, 204),
                ["track"] = Color.FromArgb(255, 177, 140, 75),
            },
            RoadOutlineColors = new Dictionary<string, Color>
            {
                ["motorway"] = Color.FromArgb(255, 196, 80, 108),
                ["motorway_link"] = Color.FromArgb(255, 196, 80, 108),
                ["trunk"] = Color.FromArgb(255, 200, 130, 100),
                ["trunk_link"] = Color.FromArgb(255, 200, 130, 100),
                ["primary"] = Color.FromArgb(255, 200, 170, 110),
                ["primary_link"] = Color.FromArgb(255, 200, 170, 110),
                ["secondary"] = Color.FromArgb(255, 210, 210, 140),
                ["secondary_link"] = Color.FromArgb(255, 210, 210, 140),
            },
            ParkColors = new Dictionary<string, Color>
            {
                ["forest"] = Color.FromArgb(255, 140, 196, 124),
                ["wood"] = Color.FromArgb(255, 140, 196, 124),
                ["scrub"] = Color.FromArgb(255, 168, 209, 150),
                ["grass"] = Color.FromArgb(255, 205, 235, 176),
                ["meadow"] = Color.FromArgb(255, 205, 235, 176),
                ["farmland"] = Color.FromArgb(255, 237, 240, 214),
                ["orchard"] = Color.FromArgb(255, 172, 225, 161),
                ["vineyard"] = Color.FromArgb(255, 172, 225, 161),
                ["recreation_ground"] = Color.FromArgb(255, 207, 252, 210),
                ["park"] = Color.FromArgb(255, 200, 250, 204),
                ["garden"] = Color.FromArgb(255, 200, 250, 204),
                ["nature_reserve"] = Color.FromArgb(255, 160, 215, 145),
            }
        };

        public static readonly MapTheme[] AllThemes = { Light, Dark, GMaps, Neon, Osm };

        public static MapTheme GetById(string id)
        {
            if (id != null)
            {
                foreach (var t in AllThemes)
                {
                    if (t.Id == id) return t;
                }
            }
            return Light;
        }
    }
}
