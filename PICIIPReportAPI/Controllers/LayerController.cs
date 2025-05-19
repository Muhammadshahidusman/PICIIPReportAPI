using Newtonsoft.Json.Linq;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading.Tasks;
using System.Web.Http;

namespace PICIIPReportAPI.Controllers
{
    public class LayerController : ApiController
    {
        private readonly string _connectionString;

        public LayerController()
        {
            // Load connection string from web.config
            _connectionString = ConfigurationManager.ConnectionStrings["PostgresConnection"].ConnectionString;
        }
        // POST api/layer/centerpoint
        [HttpPost]
        [Route("api/layer/centerpoint")]
        public IHttpActionResult GetLayerCenterPoint([FromBody] JObject requestData)
        {
            try
            {
                // Extract the 'layer' parameter from the request body
                var layer = requestData["layer"]?.ToString();

                if (string.IsNullOrEmpty(layer))
                {
                    return BadRequest("Layer parameter is required.");
                }

                // Call a method to get the center coordinates and zoom level based on the layer name
                var layerData = GetLayerCenterAndZoom(layer);

                if (layerData == null)
                {
                    return NotFound();
                }

                // Return the center coordinates and zoom level in the response
                return Ok(layerData);
            }
            catch (System.Exception ex)
            {
                return InternalServerError(ex);
            }
        }

        private LayerCenterData GetLayerCenterAndZoom(string adjustedLayer)
        {
            adjustedLayer = adjustedLayer.Replace("ss:", ""); // Ensure we handle the dynamic layer correctly

            // Build the SQL query to get the boundary, bounding box, and other details
            var sqlQuery = $@"
SELECT 
    ST_AsGeoJSON(ST_Transform(ST_Envelope(ST_Union(geom)), 4326)) AS bounding_box,
    ST_AsGeoJSON(ST_Transform(ST_Union(geom), 4326)) AS geometry,
    COUNT(*) AS row_count,
    (ST_Area(ST_Transform(ST_Union(geom), 3857)) / 4046.85642) AS area_acres,
    SUM(ST_Length(ST_Transform(geom, 3857)) / 1000) AS area_km2
FROM public.{adjustedLayer};";

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                connection.Open();

                using (var command = new NpgsqlCommand(sqlQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())  // Check if we have a result
                        {
                            // Extract data
                            var boundingBoxGeoJson = reader["bounding_box"] as string; // Bounding box as GeoJSON
                            var geometryGeoJson = reader["geometry"] as string;       // Full geometry as GeoJSON
                            var rowCount = reader.GetInt32(reader.GetOrdinal("row_count"));
                            var areaAcres = reader["area_acres"] as double?;
                            var areaKm2 = reader["area_km2"] as double?;

                            // Handle no data
                            if (string.IsNullOrEmpty(boundingBoxGeoJson) || string.IsNullOrEmpty(geometryGeoJson))
                            {
                                return null; // No valid geometry found
                            }

                            // Parse bounding box to calculate center
                            var bbox = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(boundingBoxGeoJson);
                            var centerLongitude = (double)(bbox.coordinates[0][0][0] + bbox.coordinates[0][2][0]) / 2;
                            var centerLatitude = (double)(bbox.coordinates[0][0][1] + bbox.coordinates[0][2][1]) / 2;
                            var minLongitude = (double)bbox.coordinates[0][0][0];
                            var minLatitude = (double)bbox.coordinates[0][0][1];
                            var maxLongitude = (double)bbox.coordinates[0][2][0];
                            var maxLatitude = (double)bbox.coordinates[0][2][1];
                            // Determine zoom level
                            // var zoomLevel = CalculateZoomLevel(areaAcres, areaKm2);
                            var zoomLevel = CalculateZoomLevel(minLongitude, minLatitude, maxLongitude, maxLatitude);

                            // Return layer details with geometry
                            return new LayerCenterData
                            {
                                CenterCoordinates = new double[] { centerLatitude, centerLongitude },
                                ZoomLevel = zoomLevel      // Add the full geometry as GeoJSON
                            };
                        }
                    }
                }
            }

            return null; // If no data found, return null
        }
        private int CalculateZoomLevel(double minLongitude, double minLatitude, double maxLongitude, double maxLatitude)
        {
            // Calculate diagonal distance in kilometers using Haversine formula
            var earthRadiusKm = 6371.0;

            var dLat = DegreesToRadians(maxLatitude - minLatitude);
            var dLon = DegreesToRadians(maxLongitude - minLongitude);

            var lat1 = DegreesToRadians(minLatitude);
            var lat2 = DegreesToRadians(maxLatitude);

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2) * Math.Cos(lat1) * Math.Cos(lat2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            var diagonalDistanceKm = earthRadiusKm * c;

            // Determine zoom level based on the diagonal distance
            if (diagonalDistanceKm < 0.1) return 18; // Very small area
            if (diagonalDistanceKm < 1) return 16;   // Small neighborhood
            if (diagonalDistanceKm < 5) return 14;   // Town
            if (diagonalDistanceKm < 20) return 12;  // City
            if (diagonalDistanceKm < 100) return 10; // Region
            if (diagonalDistanceKm < 500) return 8;  // State or country
            return 6;                                // Large-scale or world view
        }

        private double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        //private int CalculateZoomLevel(double? areaAcres, double? areaKm2)
        //{
        //    // Set zoom level based on area in acres or km²
        //    if (areaAcres.HasValue && areaAcres > 0)
        //    {
        //        // Determine zoom level based on area in acres
        //        if (areaAcres < 100) // Less than 1 acre
        //        {
        //            return 14; // Zoom level for areas less than 1 acre
        //        }
        //        else if (areaAcres >= 100 && areaAcres < 500) // Between 1 and 5 acres
        //        {
        //            return 12; // Zoom level for areas between 1 and 5 acres
        //        }
        //        else
        //        {
        //            return 11; // Zoom level for areas 5 acres and larger
        //        }
        //    }
        //    else if (areaKm2.HasValue && areaKm2>0)
        //    {

        //        if (areaKm2 < 10) // Less than 0.1 km² (100 m²)
        //        {
        //            return 13; // Zoom level for areas less than 0.1 km²
        //        }
        //        else if (areaKm2 >= 10 && areaKm2 < 50) // Between 0.1 km² and 0.5 km²
        //        {
        //            return 12; // Zoom level for areas between 0.1 km² and 0.5 km²
        //        }
        //        else if (areaKm2 >= 50 && areaKm2 < 100) // Between 0.1 km² and 0.5 km²
        //        {
        //            return 11; // Zoom level for areas between 0.1 km² and 0.5 km²
        //        }
        //        else if (areaKm2 >= 100 && areaKm2 < 150) // Between 0.1 km² and 0.5 km²
        //        {
        //            return 10; // Zoom level for areas between 0.1 km² and 0.5 km²
        //        }
        //        else
        //        {
        //            return 9; // Zoom level for areas 0.5 km² and larger
        //        }
        //    }
        //    return 11;
        //}
    }
    // Class to hold the center coordinates and zoom level data
    public class LayerCenterData
    {
        public double[] CenterCoordinates { get; set; } // Example: [30.663382307177407, 73.04569392948733]
        public int ZoomLevel { get; set; }  // Example: 14
    }
}
