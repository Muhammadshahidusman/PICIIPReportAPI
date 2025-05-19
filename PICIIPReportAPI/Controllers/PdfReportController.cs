using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;
using iTextSharp.text;
using iTextSharp.text.pdf;
using iTextSharp.text.html.simpleparser;
using iTextSharp.tool.xml;
using System.Web;
using Newtonsoft.Json.Linq;
using iTextSharp.tool.xml.html;
using Newtonsoft.Json;
using Org.BouncyCastle.Asn1.Ocsp;
using static System.Net.Mime.MediaTypeNames;
using System.Linq;
using static Org.BouncyCastle.Asn1.Cmp.Challenge;
using System.ComponentModel;
using System.Security.Policy;
using System.Configuration;
using Npgsql;
using System.Data;
using System.Text;
using System.Drawing.Imaging;
using static iTextSharp.tool.xml.html.HTML;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

public class PdfReportController : ApiController
{
    private static string _shapeType = "Point";
    private readonly string _connectionString;

    public PdfReportController()
    {
        // Load connection string from web.config
        _connectionString = ConfigurationManager.ConnectionStrings["PostgresConnection"].ConnectionString;
    }
    [HttpPost]
    [Route("api/landuse/update_land_use")]
    public IHttpActionResult UpdateLandUse([FromBody] SktUpdateLandUseRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.Geometry) || string.IsNullOrEmpty(request.LandUseType))
        {
            return BadRequest("Invalid request or missing geojson data");
        }

        PdfRequestModel model = new PdfRequestModel
        {
            GeoJson = request.Geometry.ToString(),
            ExistingLandtype = request.ExistingLandUseType,
            NewLandtype = request.LandUseType,
            layerName = request.layerName,
            ObjectId = request.ObjectId,
            newSubZoneTable = request.newSubZoneTable,
            parcelAction = request.parcelAction,
            newSubZoneColoum = request.newSubZoneColoum
        };

        // Prepare the result using the request data
        var result = DeleteAndUpdateLandUseData(model);

        // Return the result as JSON
        return Ok(new { success = true, message = "Operation completed successfully" });
    }

    private Result DeleteAndUpdateLandUseData(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson_(request.GeoJson);
        string polygonWkt = ConvertToWkt_(polygon); // Convert polygon to WKT format
        decimal areaInAcres;
        string geomValue = null;
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();
            // Step 1: Retrieve the geom value from the original table as WKT
            if (request.parcelAction != "selected")
            {
                using (var getGeomCommand = new NpgsqlCommand($@"
SELECT ST_AsText(geom) 
FROM {request.layerName}
WHERE objectid = @objectId;", connection))
                {
                    getGeomCommand.Parameters.AddWithValue("objectId", request.ObjectId);
                    geomValue = getGeomCommand.ExecuteScalar() as string; // Retrieve geom as WKT (string)
                }
                polygonWkt = geomValue;
            }
            // Get the area in acres of the polygon
            using (var areaCommand = new NpgsqlCommand($@"
            SELECT ST_Area(ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326)) / 4046.85642;", connection))
            {
                areaCommand.Parameters.AddWithValue("polygonWkt", polygonWkt);
                areaInAcres = Convert.ToDecimal(areaCommand.ExecuteScalar());
            }

            // Determine the existing table based on land type
            string existingTable = GetTableByLandType(request.ExistingLandtype);
            string newTable = GetTableByLandType(request.NewLandtype);

            // Step 1: Delete specific polygon from existing land type table
            using (var deleteCommand = new NpgsqlCommand($@"
            UPDATE {existingTable}
            SET geom = ST_Difference(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))
            WHERE ST_Contains(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326));", connection))
            {
                deleteCommand.Parameters.AddWithValue("polygonWkt", polygonWkt);
                deleteCommand.ExecuteNonQuery(); // Execute the update command to remove the specific polygon
            }

            // Step 2: Record the action for the deletion (with -1)
            RecordActionLog(connection, request, areaInAcres, -1);

            // Step 3: Merge polygon with existing geometry in the new land type table
            using (var mergeCommand = new NpgsqlCommand($@"
            UPDATE {newTable}
            SET geom = ST_Union(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))
            WHERE category = @category AND subzone = @subzone;", connection))
            {
                mergeCommand.Parameters.AddWithValue("polygonWkt", polygonWkt);
                mergeCommand.Parameters.AddWithValue("category", request.NewLandtype);
                mergeCommand.Parameters.AddWithValue("subzone", request.newSubZoneColoum);
                mergeCommand.ExecuteNonQuery(); // Execute merge command
            }

            // Step 4: Record the action for the addition (with 1)
            RecordActionLog(connection, request, areaInAcres, 1);

            // Additional logic for 'selected' parcelAction
            if (request.parcelAction == "selected")
            {
                using (var selectedConnection = new NpgsqlConnection(_connectionString))
                {
                    selectedConnection.Open();

                    // Step 5: Delete specific polygon from the specified layer
                    using (var deleteCommand = new NpgsqlCommand($@"
                    UPDATE {request.layerName}
                    SET geom = ST_Difference(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))
                    WHERE ST_Contains(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326));", selectedConnection))
                    {
                        deleteCommand.Parameters.AddWithValue("polygonWkt", polygonWkt);
                        deleteCommand.ExecuteNonQuery(); // Execute the update command to remove the specific polygon
                    }

                    // Step 6: Merge polygon into the new land type table for 'selected' action
                    using (var mergeCommand = new NpgsqlCommand($@"
                    WITH target_row AS (
                        SELECT geom
                        FROM {request.NewLandtype}
                        ORDER BY gid
                        LIMIT 1
                    )
                    UPDATE {request.NewLandtype}
                    SET geom = ST_Union(target_row.geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))
                    FROM target_row
                    WHERE {request.NewLandtype}.geom = target_row.geom;", selectedConnection))
                    {
                        mergeCommand.Parameters.AddWithValue("polygonWkt", polygonWkt);
                        mergeCommand.ExecuteNonQuery(); // Execute merge command
                    }
                }
            }
            else
            {
                using (var connectiondelete = new NpgsqlConnection(_connectionString))
                {
                    connectiondelete.Open();



                    if (geomValue != null)
                    {
                        using (var insertCommand = new NpgsqlCommand($@"
INSERT INTO {request.newSubZoneTable} (gid, objectid, id, name, area_arc, geom)
SELECT 
    (SELECT COALESCE(MAX(gid), 0) + 1 FROM {request.newSubZoneTable}) AS new_gid,
    (SELECT COALESCE(MAX(objectid), 0) + 1 FROM {request.newSubZoneTable}) AS new_objectid,
    id,  -- Original id from the source table
    name, 
    area_arc, 
    ST_GeomFromText(@geom, 4326)
FROM {request.layerName}
WHERE objectid = @objectId;", connectiondelete))
                        {
                            insertCommand.Parameters.AddWithValue("objectId", request.ObjectId);
                            insertCommand.Parameters.AddWithValue("geom", geomValue); // Pass WKT geom value
                            insertCommand.ExecuteNonQuery();
                        }

                        // Step 2: Delete from the original table based on ObjectId
                        using (var deleteCommand = new NpgsqlCommand($@"
DELETE FROM {request.layerName}
WHERE objectid = @objectId;", connectiondelete))
                        {
                            deleteCommand.Parameters.AddWithValue("objectId", request.ObjectId);
                            deleteCommand.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("No geom found for the specified ObjectId.");
                    }
                }
            }

        }

        // Return the result with the new land type and category
        return new Result
        {
            TableName = request.NewLandtype,
            Category = "new_category", // Update as needed
            Subzone = "new_subzone" // Update as needed
        };
    }

    private string GetTableByLandType(string landType)
    {
        switch (landType)
        {
            case "Commercial": return "commerialdecision";
            case "Residential": return "residentialdecision";
            case "Agriculture": return "agriculturedecision";
            case "Industrial": return "industrydecision";
            default: throw new ArgumentException("Invalid land type");
        }
    }

    private void RecordActionLog(NpgsqlConnection connection, PdfRequestModel request, decimal areaInAcres, int incrementDecrement)
    {
        decimal roundedAreaInAcres = Math.Round(areaInAcres, 4);
        using (var logCommand = new NpgsqlCommand($@"
        INSERT INTO action_logs 
        (GeoJson, ExistingLandtype, NewLandtype, layerName, ObjectId, newSubZone, parcelAction, increament_decrement, action_table, change_datetime, area_in_acre)
        VALUES (@geoJson, @existingLandtype, @newLandtype, @layerName, @objectId, @newSubZone, @parcelAction, @incrementDecrement, @actionTable, @timestamp, @areaInAcres);", connection))
        {
            logCommand.Parameters.AddWithValue("geoJson", request.GeoJson.ToString());
            logCommand.Parameters.AddWithValue("existingLandtype", request.ExistingLandtype);
            logCommand.Parameters.AddWithValue("newLandtype", request.NewLandtype);
            logCommand.Parameters.AddWithValue("layerName", request.layerName);
            logCommand.Parameters.AddWithValue("objectId", request.ObjectId);
            logCommand.Parameters.AddWithValue("newSubZone", request.newSubZoneColoum);
            logCommand.Parameters.AddWithValue("parcelAction", request.parcelAction);
            logCommand.Parameters.AddWithValue("incrementDecrement", incrementDecrement);
            logCommand.Parameters.AddWithValue("actionTable", incrementDecrement == -1 ? request.ExistingLandtype.ToLower() : request.NewLandtype.ToLower());
            logCommand.Parameters.AddWithValue("timestamp", DateTime.Now); // Current datetime
            logCommand.Parameters.AddWithValue("areaInAcres", roundedAreaInAcres); // Area in acres

            logCommand.ExecuteNonQuery(); // Record action
        }
    }


    [HttpPost]
    [Route("api/landuse/CheckLanduseType")]
    public IHttpActionResult CheckLanduseType([FromBody] dynamic request)
    {
        if (request == null || request.geojson == null)
            return BadRequest("Invalid request or missing geojson data");

        // Convert the dynamic request.geojson to a string and assign it to the model
        PdfRequestModel model = new PdfRequestModel();
        model.GeoJson = request.geojson.ToString(); // Directly access request.geojson

        // Prepare the result using the request data
        var result = GetZoneExistDataFromDatabase_(model);

        // Return the result as JSON
        return Ok(result);
    }
    [HttpPost]
    [Route("api/pdfreport/approveReport")]
    public IHttpActionResult ApproveReport([FromBody] PdfRequestModel request)
    {
        // Check if the request is null
        if (request == null)
        {
            return Ok(new { success = false, message = "Invalid request" });
        }

        // Check if the call type is "approve"
        if (request.calltype == "approve")
        {
            var result = GetZoneExistDataFromDatabase(request);
            string Zone = result?.Category ?? "N/A";
            string subzone = result?.Subzone ?? "N/A";
            if (!string.IsNullOrEmpty(Zone))
            {
                Zone = char.ToUpper(Zone[0]) + Zone.Substring(1);
            }
            //< p id = ""signature - auth"" > Signature and Authorization: __________________ </ p >
            var subZoneParts = request.SubZone?.Split('-');
            var subZoneType = subZoneParts != null && subZoneParts.Length > 0 ? subZoneParts[0].Trim() : string.Empty;
            var permittedpermissibleName = subZoneParts != null && subZoneParts.Length > 0 ? subZoneParts[1].Trim() : string.Empty;
            string usesResult = "prohibited";

            if (Zone != "--")
            {
                usesResult = CheckUsesExistorNot(Zone, permittedpermissibleName)?.ToLower() ?? "prohibited";
            }

            // Check if both Zone and Subzone are valid
            if (Zone == "N/A" || subzone == "N/A")
            {
                return Ok(new { success = true, message = "Operation Failed.Invalid Zone or Subzone information", zone = Zone, subzone = subzone });
            }
            else if (usesResult== "prohibited")
            {
                return Ok(new { success = true, message = "Operation Failed.Result Uses is prohibited", zone = Zone, subzone = subzone });
            }
            else
            {
                var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);
                string polygonWkt = ConvertToWkt(polygon);
                string layertblname = GetLayernamebyPolygonWKT(polygonWkt);
                string subzonetable=  GetSubzoneTablename(subzone);
                PdfRequestModel model = new PdfRequestModel
                {
                    GeoJson = request.GeoJson.ToString(),
                    ExistingLandtype = request.AppliedLandUse.Replace("Zone", "").Trim(),
                    NewLandtype = Zone,
                    layerName = layertblname,
                    ObjectId = 0,
                    newSubZoneTable = subzonetable,
                    parcelAction = "selected",
                    newSubZoneColoum = subzone
                };
                if(Zone.Trim()!= request.AppliedLandUse.Replace("Zone", "").Trim() && layertblname!=null)
                {
                    //  var result1 = DeleteAndUpdateLandUseData(model);
                    var resultEmail = UpdatestatusPlotRequest("Approved", request.plotId);
                    return Ok(new { success = true, message = "Request Approved successfully and Email Notification sent to Applicant",username = resultEmail.Username,
                        email = resultEmail.Email
                    });
                }
                else if (Zone.Trim() == request.AppliedLandUse.Replace("Zone", "").Trim())
                {
                    var resultEmail = UpdatestatusPlotRequest("Approved", request.plotId);
                    return Ok(new
                    {
                        success = true,
                        message = "Request Approved successfully and Email Notification sent to Applicant",
                        username = resultEmail.Username,
                        email = resultEmail.Email
                    });
                }
                else
                {
                    return Ok(new { success = false, message = "Operation Failed.Applied Category and Appr" });
                }
            }
        }

        // If the call type is not "approve", you might want to handle that case as well
        return Ok(new { success = false, message = "Invalid call type" });
    }
    private (string Username, string Email) UpdatestatusPlotRequest(string status, int plotId)
    {
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to update the request_status based on the plot ID
            using (var updateCommand = new NpgsqlCommand(@"
        UPDATE plot_requests
        SET request_status = @status
        WHERE id = @plotId", connection))
            {
                // Add parameters to prevent SQL injection
                updateCommand.Parameters.AddWithValue("@status", status);
                updateCommand.Parameters.AddWithValue("@plotId", plotId);

                // Execute the update command
                updateCommand.ExecuteNonQuery();
            }

            // Prepare the SQL command to fetch username and email
            using (var selectCommand = new NpgsqlCommand(@"
        SELECT username, email
        FROM public.auth_user
        INNER JOIN plot_requests ON plot_requests.submitted_by = auth_user.username
        WHERE plot_requests.id = @plotId", connection))
            {
                selectCommand.Parameters.AddWithValue("@plotId", plotId);

                using (var reader = selectCommand.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        // Read the username and email from the result
                        string username = reader.GetString(0); // Username
                        string email = reader.GetString(1);    // Email

                        return (username, email);
                    }
                }
            }
        }

        // Return default values if no data is found
        return (null, null);
    }

    private string GetLayernamebyPolygonWKT(string PolygonWKT)
    {
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to get all table names from `subzones`
            using (var command = new NpgsqlCommand(@"
            SELECT subzone_table 
            FROM public.subzones", connection))
            {
                // Retrieve all table names
                using (var reader = command.ExecuteReader())
                {
                    var tableNames = new List<string>();

                    while (reader.Read())
                    {
                        tableNames.Add(reader["subzone_table"].ToString());
                    }

                    reader.Close();

                    // Iterate through each table and check for a containing geometry
                    foreach (var tableName in tableNames)
                    {
                        // Query to check if the `PolygonWKT` is contained in the current table's geom column
                        using (var searchCommand = new NpgsqlCommand($@"
                        SELECT 1
                        FROM {tableName}
                        WHERE ST_Contains(geom, ST_SetSRID(ST_GeomFromText(@PolygonWKT), 4326))
                        LIMIT 1", connection))
                        {
                            // Parameterize the PolygonWKT input to avoid SQL injection
                            searchCommand.Parameters.AddWithValue("@PolygonWKT", PolygonWKT);

                            // Execute the query and check if a match is found
                            var exists = searchCommand.ExecuteScalar();

                            if (exists != null) // If the query returns a row
                            {
                                return tableName; // Return the table name with the match
                            }
                        }
                    }
                }
            }
        }

        // Return null if no matching result is found in any table
        return null;
    }

    private string GetSubzoneTablename(string subzone)
    {
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();
            // Prepare the SQL command to search for a matching `use_type` in the `zoning_uses` table
            using (var command = new NpgsqlCommand(@"
            SELECT subzone_table
            FROM public.subzones
            WHERE subzone_name = @subzone;", connection))
            {
                // Set parameters to prevent SQL injection
                command.Parameters.AddWithValue("subzone", subzone);

                using (var reader = command.ExecuteReader())
                {
                    // If a result is found, read the data
                    if (reader.Read())
                    {

                        return reader["subzone_table"].ToString();

                    }
                }
            }
        }

        // Return null if no matching result is found
        return null;
    }

    [HttpPost]
    [Route("api/pdfreport/generate")]

    public HttpResponseMessage GenerateReport([FromBody] PdfRequestModel request)
    {
        if (request == null)
            return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Invalid request");

        try
        {
            string htmlContent = "";
            if (!request.is_superuser && request.ApplicationStatus != "Approved" && request.ApplicationStatus != "Rejected")
            {
                htmlContent = GenerateUserHtmlContent(request);
            }
            else
            {
                htmlContent = GenerateHtmlContent(request);
            }
                if (string.IsNullOrWhiteSpace(htmlContent))
                {
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Generated HTML content is empty.");
                }

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    Document pdfDoc = new Document(PageSize.A4, 40f, 40f, 60f, 50f);
                    PdfWriter writer = PdfWriter.GetInstance(pdfDoc, memoryStream);
                    writer.PageEvent = new CustomFooter();
                    pdfDoc.Open();

                    try
                    {
                        using (StringReader sr = new StringReader(htmlContent))
                        {
                            XMLWorkerHelper.GetInstance().ParseXHtml(writer, pdfDoc, sr);
                        }
                    }
                    catch (Exception ex)
                    {
                        return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, $"Error generating PDF: {ex.Message}");
                    }

                    pdfDoc.Close();

                    var response = Request.CreateResponse(HttpStatusCode.OK);
                    response.Content = new ByteArrayContent(memoryStream.ToArray());
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                    {
                        FileName = "LandUseDecisionReport.pdf"
                    };

                    return response;
                }
            
        }
        catch (Exception ex)
        {
            return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, $"Unexpected error: {ex.Message}");
        }
    }

    private List<(double, double)> ExtractCoordinatesFromGeoJson_(string geoJson)
    {
        var coordinatesList = new List<(double, double)>();

        // Parse the GeoJSON string
        var geoJsonObject = JObject.Parse(geoJson);

        // Navigate to the coordinates array
        var coordinates = geoJsonObject["geometry"]?["coordinates"]?[0];

        if (coordinates != null)
        {
            foreach (var coordinate in coordinates)
            {
                double longitude = coordinate[0].Value<double>();
                double latitude = coordinate[1].Value<double>();

                coordinatesList.Add((longitude, latitude));
            }
        }

        return coordinatesList;
    }
    private Result GetZoneExistDataFromDatabase_(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson_(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // List of table names to check
            var tableNames = new List<string>
        {
            "residentialdecision",
            "agriculturedecision",
            "commerialdecision",
           // "industrydecision",
            // Add other tables as needed
        };

            foreach (var tableName in tableNames)
            {
                // Prepare the SQL command to check if the polygon is contained within any geometry in the current table
                using (var command = new NpgsqlCommand($@"
                SELECT category,subzone
                FROM {tableName}
                WHERE ST_Contains(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326));", connection))
                {
                    // Convert the polygon coordinates to WKT (Well-Known Text) format
                    string polygonWkt = ConvertToWkt_(polygon);
                    command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                    using (var reader = command.ExecuteReader())
                    {
                        // Check if any results were returned
                        if (reader.Read())
                        {
                            return new Result
                            {
                                TableName = tableName,
                                Category = reader["category"].ToString(),
                                Subzone = reader["subzone"].ToString()
                            };
                        }
                    }
                }
            }
        }

        // Return null if no matching records were found in any table
        return null;
    }
    private Result GetZoneExistDataFromDatabase(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // List of table names to check
            var tableNames = new List<string>
        {
            "residentialdecision",
            "agriculturedecision",
            "commerialdecision",
            // Add other tables as needed
        };

            // Dictionaries to store results
            var categoryAreas = new Dictionary<string, List<double>>();
            var categoryCoordinates = new Dictionary<string, List<string>>();
            var subzones = new HashSet<string>();
            var categoryColors = new Dictionary<string, string>
        {
            { "Residential", "0xFFFF00" }, // Yellow
            { "Commercial", "0xFF0000" }, // Red
            { "Industrial", "0x800080" }, // Purple
            { "Agriculture", "0x00FF00" } // Green
        };

            foreach (var tableName in tableNames)
            {
                using (var command = new NpgsqlCommand($@"
                SELECT 
                    category,
                    subzone,
                    COALESCE(ST_Area(ST_Intersection(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))) / 4046.86, 0) AS area_acres,
                    ST_AsText(ST_Intersection(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))) AS intersect_geom
                FROM {tableName}
                WHERE ST_Intersects(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326));", connection))
                {
                    string polygonWkt = ConvertToWkt(polygon);
                    command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string category = reader["category"].ToString();
                            string subzone = reader["subzone"].ToString();
                            double area = reader["area_acres"] != DBNull.Value ? Convert.ToDouble(reader["area_acres"]) : 0;
                            string intersectGeom = reader["intersect_geom"].ToString();

                            if (area > 0)
                            {
                                string areaString = area.ToString("E5", CultureInfo.InvariantCulture);
                                string firstFiveChars = areaString.Length > 5 ? areaString.Substring(0, 5) : areaString;
                                area = Math.Round(Convert.ToDouble(firstFiveChars), 2);
                                // Add area to the category's list
                                if (!categoryAreas.ContainsKey(category))
                                {
                                    categoryAreas[category] = new List<double>();
                                }
                                categoryAreas[category].Add(Math.Round(area, 2));

                                // Add subzone to the set
                                subzones.Add(subzone);

                                // Add coordinates for the category
                                if (!categoryCoordinates.ContainsKey(category))
                                {
                                    categoryCoordinates[category] = new List<string>();
                                }
                                if (!string.IsNullOrEmpty(intersectGeom))
                                {
                                    categoryCoordinates[category].Add(ConvertWktToCoordinates(intersectGeom));
                                }
                            }
                        }
                    }
                }
            }

            // Build the Result object
            var result = new Result
            {
                Category = string.Join(", ", categoryAreas.Keys),
                Subzone = string.Join(", ", subzones),
                Area = string.Join(", ", categoryAreas.SelectMany(kvp => kvp.Value).Select(a => a.ToString("F2"))),
                CategoryColors = string.Join(", ", categoryColors.Where(kvp => categoryAreas.ContainsKey(kvp.Key))
                                                                   .Select(kvp => kvp.Value)) // Comma-separated color codes
            };

            // If more than one category, build and include the rawMapUrl
            if (categoryAreas.Count > 1)
            {
                var rawMapUrl = "https://maps.googleapis.com/maps/api/staticmap?size=280x230&maptype=hybrid";

                foreach (var category in categoryCoordinates.Keys)
                {
                    if (categoryColors.TryGetValue(category, out string color))
                    {
                        string categoryPath = string.Join("|", categoryCoordinates[category]);
                        rawMapUrl += $"&path=color:{color}|weight:5|{categoryPath}";
                    }
                }

                // Append zoom level and API key
                rawMapUrl += "&zoom=15&key=AIzaSyC9SY7c76e7gZ5qnpo63Rdj-KmyAyQFqHs";

                result.RawMapUrl = rawMapUrl; // Add the URL to the result
            }

            return result;
        }
    }
    // Helper method to convert WKT to coordinates
    private string ConvertWktToCoordinates(string wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
            throw new ArgumentException("WKT input cannot be null or empty.");

        if (!wkt.StartsWith("POLYGON((") || !wkt.EndsWith("))"))
            throw new FormatException("Invalid WKT format. Expected POLYGON((...))");

        // Remove the POLYGON(( and )) parts
        string trimmedWkt = wkt.Replace("POLYGON((", "").Replace("))", "");

        // Split the coordinates into individual points based on commas
        var points = trimmedWkt.Split(',');

        // Convert each point from "x y" to "y,x"
        var convertedPoints = points.Select(point =>
        {
            var coords = point.Trim().Split(' '); // Split by space to separate x and y
            if (coords.Length != 2)
                throw new FormatException($"Invalid coordinate format: {point}");

            // Reverse the order from "x y" (longitude latitude) to "latitude,longitude"
            return $"{coords[1]},{coords[0]}";
        });

        // Join the converted points with the pipe separator for Google Maps API
        return string.Join("|", convertedPoints);
    }

    private string FindAbuttingRoad(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to check if the polygon is contained within any geometry in the listaroad table
            using (var command = new NpgsqlCommand($@"
            SELECT name_1 AS AbutingRoad
            FROM public.listaroad
            WHERE ST_Intersects(
                geom, 
                ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326)
            );", connection))
            {
                // Convert the polygon coordinates to WKT (Well-Known Text) format
                string polygonWkt = ConvertToWkt(polygon);
                command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                using (var reader = command.ExecuteReader())
                {
                    // Check if any results were returned
                    if (reader.Read())
                    {
                        // Return the road name if found
                        return reader["AbutingRoad"].ToString();
                    }
                }
            }
        }

        // Return null if no matching records were found
        return null;
    }
    private string CheckEBABoundary(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to check if the polygon is contained within any geometry in the listaroad table
            using (var command = new NpgsqlCommand($@"
            SELECT name
            FROM public.eba_boundary
            WHERE ST_Intersects(
                geom, 
                ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326)
            );", connection))
            {
                // Convert the polygon coordinates to WKT (Well-Known Text) format
                string polygonWkt = ConvertToWkt(polygon);
                command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                using (var reader = command.ExecuteReader())
                {
                    // Check if any results were returned
                    if (reader.Read())
                    {
                        // Return the road name if found
                        return reader["name"].ToString();
                    }
                }
            }
        }

        // Return null if no matching records were found
        return null;
    }
    private string CheckEBAVacant(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to check if the polygon is contained within any geometry in the listaroad table
            using (var command = new NpgsqlCommand($@"
            SELECT name
            FROM public.vacant_eba
            WHERE ST_Intersects(
                geom, 
                ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326)
            );", connection))
            {
                // Convert the polygon coordinates to WKT (Well-Known Text) format
                string polygonWkt = ConvertToWkt(polygon);
                command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                using (var reader = command.ExecuteReader())
                {
                    // Check if any results were returned
                    if (reader.Read())
                    {
                        // Return the road name if found
                        return reader["landuse"].ToString();
                    }
                }
            }
        }

        // Return null if no matching records were found
        return null;
    }
    private string CheckExistingLandUse(PdfRequestModel request)
    {
        // Extract the polygon from GeoJSON
        var polygon = ExtractCoordinatesFromGeoJson(request.GeoJson);

        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();

            // Prepare the SQL command to check if the polygon intersects and get the largest area
            using (var command = new NpgsqlCommand($@"
        SELECT main_category AS landuse
        FROM public.existing_landuse
        WHERE ST_Intersects(
            geom, 
            ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326)
        )
        ORDER BY ST_Area(ST_Intersection(geom, ST_SetSRID(ST_GeomFromText(@polygonWkt), 4326))) DESC
        LIMIT 1;", connection))
            {
                // Convert the polygon coordinates to WKT (Well-Known Text) format
                string polygonWkt = ConvertToWkt(polygon);
                command.Parameters.AddWithValue("polygonWkt", polygonWkt);

                using (var reader = command.ExecuteReader())
                {
                    // Return the landuse with the largest intersection area
                    return reader.Read() ? reader["landuse"].ToString() : null;
                }
            }
        }
    }

    private string CheckUsesExistorNot(string category, string search)
    {
        // Dynamically remove "Zone" (case-insensitive) and any preceding space
        category = Regex.Replace(category, @"\s*Zone", string.Empty, RegexOptions.IgnoreCase);
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            connection.Open();
            // Prepare the SQL command to search for a matching `use_type` in the `zoning_uses` table
            using (var command = new NpgsqlCommand(@"
            SELECT use_type
            FROM public.zoning_uses
            WHERE category = @category AND use_description ILIKE @search;", connection))
            {
                // Set parameters to prevent SQL injection
                command.Parameters.AddWithValue("category", category);
                command.Parameters.AddWithValue("search", "%" + search + "%");

                using (var reader = command.ExecuteReader())
                {
                    // If a result is found, read the data
                    if (reader.Read())
                    {

                        return reader["use_type"].ToString();
                       
                    }
                }
            }
        }

        // Return null if no matching result is found
        return null;
    }
    private string ConvertToWkt_(List<(double, double)> coordinates)
    {
        StringBuilder wkt = new StringBuilder("POLYGON((");

        foreach (var (longitude, latitude) in coordinates)
        {
            wkt.Append($"{longitude} {latitude}, ");
        }

        // Remove the trailing comma and space, then close the polygon with the first point
        wkt.Length -= 2;
        wkt.Append("))");

        return wkt.ToString();
    }
    private string ConvertToWkt(List<double[]> coordinates)
    {
        // Check for non-empty coordinates
        if (coordinates == null || coordinates.Count == 0)
            throw new ArgumentException("Coordinates cannot be null or empty.");

        // Convert each coordinate pair to "longitude latitude" format for WKT
        var coordinatePairs = coordinates.Select(coord => $"{coord[1]} {coord[0]}"); // assuming [latitude, longitude]
        return $"POLYGON(({string.Join(", ", coordinatePairs)}))";
    }
    public class Result
    {
        public string TableName { get; set; }
        public string Category { get; set; }
        public string Subzone { get; set; }
        public string Area { get; set; }
        public string CategoryColors { get; set; }
        public string RawMapUrl { get; set; }
    }
    public class CustomFooter : PdfPageEventHelper
    {
        public override void OnEndPage(PdfWriter writer, Document document)
        {
            PdfContentByte cb = writer.DirectContent;

            // Set the line position
            float lineY = document.BottomMargin - 10;

            // Draw a horizontal line before the page number
            cb.MoveTo(document.LeftMargin, lineY);
            cb.LineTo(document.PageSize.Width - document.RightMargin, lineY);
            cb.Stroke();

            // Create a table for the page number
            PdfPTable footerTable = new PdfPTable(1);
            footerTable.TotalWidth = document.PageSize.Width - document.LeftMargin - document.RightMargin;
            footerTable.DefaultCell.Border = 0;

            // Add the dynamic page number to the footer
            string pageNumberText = $" {writer.PageNumber} | P a g e";
            PdfPCell footerCell = new PdfPCell(new Phrase(pageNumberText, new Font(Font.FontFamily.HELVETICA, 10, Font.NORMAL)));
            footerCell.HorizontalAlignment = Element.ALIGN_LEFT;
            footerCell.Border = Rectangle.NO_BORDER;

            // Add the cell to the table
            footerTable.AddCell(footerCell);

            // Write the table at the bottom of the page
            footerTable.WriteSelectedRows(0, -1, document.LeftMargin, lineY - 10, writer.DirectContent);
        }
    }

    private string GenerateUserHtmlContent(PdfRequestModel request)
    {

        string logo1Path = HttpContext.Current.Server.MapPath("~/images/punjabgovt.png");
        string logo2Path = HttpContext.Current.Server.MapPath("~/images/PICIIP.png");
        var coordinates = ExtractCoordinatesFromGeoJson(request.GeoJson);
        var centerCoordinate = coordinates.First();
        double longitude = centerCoordinate[1];
        double latitude = centerCoordinate[0];
        // Initialize rawMapUrl
        string rawMapUrl = null;
        bool ResidentialCategory = request.LandUseSearch?.Any(item =>
     item.Category.Equals("Residential", StringComparison.OrdinalIgnoreCase) ||
     item.Category.Equals("Residential Zone", StringComparison.OrdinalIgnoreCase)) ?? false;

        string pathCoordinates = string.Join("|", coordinates.Select(c => $"{c[0]},{c[1]}"));
        string searchArea = request.LandUseSearch[0].SearchText;

        string colorCode = "";
        // Add relevant LandUsePlanItems based on the category
        if (ResidentialCategory)
        {
            colorCode = "color:0xFFFF00";
        }
        if (request.LandUseSearch.Any(item =>
    item.Category.Equals("Commercial", StringComparison.OrdinalIgnoreCase) ||
    item.Category.Equals("Commercial Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0xFF0000";
        }
        if (request.LandUseSearch.Any(item =>
 item.Category.Equals("Industrial", StringComparison.OrdinalIgnoreCase) ||
 item.Category.Equals("Industrial Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0x800080";
        }
        if (request.LandUseSearch.Any(item =>
    item.Category.Equals("Agriculture", StringComparison.OrdinalIgnoreCase) ||
    item.Category.Equals("Agriculture Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0x00FF00";
        }
        int zoomLevel = 16;
        if (_shapeType == "Point" || _shapeType == "MultiPoint")
        {
            // Generate markers for Point or MultiPoint geometries
            string markers = string.Join("|", coordinates.Select(c => $"{colorCode}|{c[0]},{c[1]}"));
            rawMapUrl = $"https://maps.googleapis.com/maps/api/staticmap?" +
                        $"size=280x230&" +
                        $"maptype=hybrid&" +
                        $"markers={markers}&" +
                        $"zoom={zoomLevel}&" +
                        $"key=AIzaSyC9SY7c76e7gZ5qnpo63Rdj-KmyAyQFqHs";
        }
        else
        {
            // Handle shapes like Polygon or LineString with path drawing
            rawMapUrl = $"https://maps.googleapis.com/maps/api/staticmap?" +
                        $"size=280x230&" +
            $"maptype=hybrid&" +
                        $"path={colorCode}|weight:5|{pathCoordinates}&" +
                        $"zoom={zoomLevel}&" +
                        $"key=AIzaSyC9SY7c76e7gZ5qnpo63Rdj-KmyAyQFqHs";
        }
        string zone = request.AppliedLandUse.Replace("Zone", "").Trim();
        string htmlContent = $@"
<html>
    <head>
        <style>
            html, body {{
                font-family: Arial, sans-serif;
                font-size: 12px;
                margin: 0;
                padding: 0;
                position: relative;
            }}
            h1 {{
                text-align: center;
                font-size: 22px;
                font-weight: bold;
                margin: 0; /* Remove default margin */
            }}
            h2 {{
                margin-top: 20px; /* Add some space above h2 */
            }}
            table {{
                width: 100%;
                border-collapse: collapse;
                margin-bottom: 20px;
            }}
            th, td {{
                padding: 8px;
                text-align: left;
                border: 1px solid #ddd; /* This applies to other tables, not the logo table */
                font-size: 12px;
            }}
            th {{
                background-color: #f2f2f2;
            }}
            .logo-table {{
                width: 100%;
               border: none !important; /* Ensure no border */
                margin: 0;
                padding: 0;
                text-align: center;
                border-collapse: collapse; /* Ensure no space between borders */
            }}
            .logo-table td {{
                border: none !important; /* Ensure no border for logo table cells */
                padding: 0;
            }}
            .logo-left {{
                text-align: left;
                padding-left: 10px;
            }}
            .logo-right {{
                text-align: right;
                padding-right: 10px;
            }}
            .address-row td {{
                height: 70px !important;
            }}
            .coloum-row td {{
                height: 30px !important;
            }}
            .field-row td {{
                width: 180px !important;
            }}
            #signature-auth {{
                display: none;
            }}
        </style>
    </head>
    <body>
     <table class='logo-table' style='border-collapse: collapse; width: 100%; border: 1px solid white;'>
    <tr>
        <td class='logo-left' style='border: 1px solid white;'>
            <img src='file:///{logo1Path}' alt='Punjab Government Logo' style='width: 100px; height: 85px;' />
        </td>
        <td style='border: 1px solid white; text-align: center;'>
            <h1>LAND USE REPORT</h1>
        </td>
        <td class='logo-right' style='border: 1px solid white;'>
            <img src='file:///{logo2Path}' alt='PICIIP Logo' style='width: 100px; height: 100px;' />
        </td>
    </tr>
</table>
        <h2 class='left-aligned'>APPLICATION DETAILS</h2>

      <table class='left-aligned'>
    <tr>
        <th class='field-row' style='width: 120px;'>Field</th>
        <th style='width: 200px;'>Details</th>
        <th>Location</th>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 100px;'>Applicant Name</td>
        <td>{request.ApplicantName}</td>
        <td rowspan='10'>
Latitude / Longitude: {latitude.ToString()} / {longitude.ToString()}
            <img src='{rawMapUrl}' alt='Map showing location' />
        </td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Applicant CNIC</td>
        <td>{request.ApplicantCNIC}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Application ID</td>
        <td>{request.ApplicationId}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Date of Application</td>
        <td>{request.ApplicationDate}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Contact No.</td>
        <td>{request.ContactDetails}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Email</td>
        <td>{request.Email}</td>
    </tr>
<tr class='coloum-row'>
    <td style='width: 120px;'>Applied Land Use</td>
    <td>
        {(request.LandUseSearch != null && request.LandUseSearch.Count > 0
            ? $"{request.LandUseSearch[0].SearchText} (<b>Category:</b> {request.AppliedLandUse.Replace("Zone", "").Trim()})"
            : "N/A")}
    </td>
</tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Area</td>
        <td>{request.SiteArea}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Abutting Road</td>
        <td>{request.AbutingRoad}</td>
    </tr>
   <tr class='coloum-row'>
        <td style='width: 120px;'>Site Situation</td>
        <td>{request.existingsituation}</td>
    </tr>
</table>
      
<h2 style='text-align:left; margin-top: 20px; margin-bottom: 10px;'>Remarks:-</h2>
<p style=""margin-top: 0; margin-bottom: 10px; font-size: 16px;"">
  Your application has been submitted successfully. Your case will be
processed after scrutiny of the submitted documents and under applicable rules.</p>

<br/>

 
    </body>
</html>";


        return htmlContent;
    }
    private string GenerateHtmlContent(PdfRequestModel request)
    {
        // Create and populate the LandUsePlanItems list
        List<LandUsePlanItem> landUsePlanItems = request.LandUsePlanItems ?? new List<LandUsePlanItem>();
        List<LandUsePlanItem> landUsePlanItems1 = request.LandUsePlanItems ?? new List<LandUsePlanItem>();
        // Extract coordinates from GeoJSON
        var coordinates = ExtractCoordinatesFromGeoJson(request.GeoJson);
        if (coordinates == null || !coordinates.Any())
        {
            throw new InvalidOperationException("Invalid or empty GeoJSON data.");
        }

       
        string searchArea = request.LandUseSearch[0].SearchText;

       
        bool isPermitted = false;
        bool isPermissible = false;
        bool isProhibited = false;

        if (isPermitted)
        {
            request.ApplicationStatus = "Permitted";
        }
        else if (isPermissible)
        {
            request.ApplicationStatus = "Permissible";
        }
        else if (isProhibited)
        {
            request.ApplicationStatus = "prohibited";
        }
        else
        {
            request.ApplicationStatus = "";
        }
        // Generate HTML content based on the request parameters
       
        string logo1Path = HttpContext.Current.Server.MapPath("~/images/punjabgovt.png");
        string logo2Path = HttpContext.Current.Server.MapPath("~/images/PICIIP.png");
        string unchekedimg = HttpContext.Current.Server.MapPath("~/images/uncheked.png");
        string checkbox = HttpContext.Current.Server.MapPath("~/images/checkbox.png");
        var centerCoordinate = coordinates.First();
        double longitude = centerCoordinate[1];
        double latitude = centerCoordinate[0];

        // Swap the order of the coordinates
        // Ensure coordinates are ordered as latitude, longitude (Google Maps expects this format)
        coordinates = coordinates.Select(c => new[] { c[0], c[1] }).ToList(); // No need to swap further, lat first

        // Generate path coordinates for shapes
        string pathCoordinates = string.Join("|", coordinates.Select(c => $"{c[0]},{c[1]}"));

        // Initialize rawMapUrl
      

        var result = GetZoneExistDataFromDatabase(request);
        string Zone = result?.Category ?? "N/A";
        string subzone = result?.Subzone ?? "N/A";
        if (!string.IsNullOrEmpty(Zone))
        {
            Zone = char.ToUpper(Zone[0]) + Zone.Substring(1);
        }
        //< p id = ""signature - auth"" > Signature and Authorization: __________________ </ p >
        var subZoneParts = request.SubZone?.Split('-');
        var subZoneType = subZoneParts != null && subZoneParts.Length > 0 ? subZoneParts[0].Trim() : string.Empty;
        var permittedpermissibleName = subZoneParts != null && subZoneParts.Length > 0 ? subZoneParts[1].Trim() : string.Empty;
        string usesResult = "prohibited";
        string htmlTable = null;
        string FoundCategory = "";
        if (result != null)
        {
            string categories = result.Category; // e.g., "Residential,Commercial"
            string areas = result.Area; // e.g., "9.125,4.954"

            // Initialize the HTML table
            htmlTable = "<table style='width: 100%; border-collapse: collapse; float:right !important;'>";
            htmlTable += "<tr><th style='width:20.5%;'>Color Code</th><th style='width:35%;'>Category</th><th style='width:25%;'>Area (Acres)</th><th style='width:19%;'>% age</th></tr>";


            // Variable to store the category with the highest percentage
            double highestPercentage = 0.0; // Variable to store the highest percentage

            if (categories.Contains(",") && areas.Contains(","))
            {
                // Split the categories and areas into arrays
                string[] categoryArray = categories.Split(',');
                string[] areaArray = areas.Split(',');
                string[] categoryColor = result.CategoryColors.Split(',');
                // Convert areas to double for calculations
                double[] areaValues = Array.ConvertAll(areaArray, double.Parse);

                // Calculate the total area
                double totalArea = areaValues.Sum();
                bool isfound = false;
                // Build the table rows
                for (int i = 0; i < categoryArray.Length; i++)
                {
                    // Calculate the percentage for each area
                    double percentage = (areaValues[i] / totalArea) * 100;

                    if (categoryArray[i].Trim() == request.AppliedLandUse.Replace("Zone", "").Trim())
                    {
                        FoundCategory = categoryArray[i].Trim();
                        isfound = true;
                    }
                    // Check if this percentage is the highest so far
                    if (percentage > highestPercentage && !isfound)
                    {
                        highestPercentage = percentage;
                        FoundCategory = categoryArray[i].Trim();
                    }
                    string colorcode_ = categoryColor[i].Trim();
                    // Add a new row to the table
                    htmlTable += "<tr>";
                    htmlTable += $"<td style='background-color:{categoryColor[i].Trim().Replace("0x", "#")}; width:20px; height:20px; border:1px solid #000;'></td>";
                    htmlTable += $"<td>{categoryArray[i].Trim()}</td>"; // Category
                    htmlTable += $"<td>{areaValues[i]:F3}</td>"; // Area formatted to 3 decimal places
                    htmlTable += $"<td>{percentage:F2}%</td>"; // Percentage formatted to 2 decimal places
                    htmlTable += "</tr>";
                }
            }
            else
            {
                // Handle the case where there is only one category and area
                FoundCategory = categories.Trim();
                highestPercentage = 100.0;

                htmlTable += "<tr>";
                htmlTable += $"<td>{categories.Trim()}</td>";
                htmlTable += $"<td>{areas.Trim()}</td>";
                htmlTable += "<td>100.00%</td>"; // Assuming 100% for a single entry
                htmlTable += "</tr>";
            }

            htmlTable += "</table>";

            // Assign the category with the highest percentage to request.LandUseSearch[0].Category
            if (request.LandUseSearch != null && request.LandUseSearch.Count > 0)
            {
                request.LandUseSearch[0].Category = FoundCategory;
                Zone = FoundCategory;
            }

            // Now you can use htmlTable in your PDF generation logic
        }
        if (Zone != "--")
        {
            usesResult = CheckUsesExistorNot(Zone, permittedpermissibleName)?.ToLower() ?? "prohibited";
        }
        
        var ListARoad = FindAbuttingRoad(request);
       var EbaBoundary = CheckEBABoundary(request);
        string subzoneColoumName = "Sub Zone:";
        if(!string.IsNullOrWhiteSpace(EbaBoundary))
        {
            Zone = "Inside EBA";
            subzoneColoumName = "Existing Land Use:";
            var existing_landuse = CheckExistingLandUse(request);
            if (!string.IsNullOrWhiteSpace(existing_landuse))
            {
                subzone = existing_landuse;
            }
            //else
            //{
            //    var EbaVacant = CheckEBAVacant(request);
            //    if (!string.IsNullOrWhiteSpace(EbaVacant))
            //    {
            //        subzone = EbaVacant;
            //    }
            //}
            // Case 01
            if (ListARoad !=null && !string.IsNullOrEmpty(ListARoad.ToString()) &&  request.AppliedLandUse.Replace("Zone", "").Trim()== "Commercial")
            {
                if (subzone == "Park" || subzone == "State land")
                {
                    usesResult = "prohibited"; // This assignment is optional, as it's already the default.
                }
                else 
                {
                    usesResult = "Permitted";
                }
            }

            // Case 02
            if (ListARoad == null && subzone == "Commercial" && request.AppliedLandUse.Replace("Zone", "").Trim() == "Commercial")
            {
                usesResult = "Permitted";
            }
            // Case C: Residential (Any Category) Along List A
            if (ListARoad != null && !string.IsNullOrEmpty(ListARoad.ToString()) && subzone == "Residential" && request.AppliedLandUse.Replace("Zone", "").Trim() == "Residential")
            {
                usesResult = "Permitted";
            }
            // Case D: Residential (Any Category) Along List B
            if (ListARoad == null && subzone == "Residential" && request.AppliedLandUse.Replace("Zone", "").Trim() == "Residential")
            {
                usesResult = "Permitted";
            }
            // Case E: Industry (Any Category) Along List A/B
            if (subzone == "Industry" && request.AppliedLandUse.Replace("Zone", "").Trim() == "Industry")
            {
                usesResult = "Permitted";
            }
            // Case G: Agriculture (Any category) Inside EBA
            if (request.AppliedLandUse.Replace("Zone", "").Trim() == "Agriculture")
            {
                usesResult = "prohibited";
            }
        }
        string ConverionFeeText = "";
        if (usesResult != "prohibited")
        {
            ConverionFeeText = "The Case may be processed for land use conversion as per the applicable conversion fee.";
        }
        if (Zone == "Residential" || Zone == "Commercial" || Zone == "Industrial" || Zone == "Industry" || Zone == "Agriculture")
        {
            request.LandUseSearch[0].Category = Zone;
        }
        else if (subzone == "Residential" || subzone == "Commercial" || subzone == "Industrial" || subzone == "Industry" || subzone == "Agriculture")
        {
            request.LandUseSearch[0].Category = subzone;
        }
      
        // Separate check for 'Residential' category
        bool ResidentialCategory = request.LandUseSearch?.Any(item =>
       item.Category.Equals("Residential", StringComparison.OrdinalIgnoreCase) ||
       item.Category.Equals("Residential Zone", StringComparison.OrdinalIgnoreCase)) ?? false;
        string colorCode = "";
        // Add relevant LandUsePlanItems based on the category
        if (ResidentialCategory)
        {
            colorCode = "color:0xFFFF00";
            landUsePlanItems1.Add(new LandUsePlanItem
            {
                Category = "Residential Zone",
                PermittedUses = "• Detached house<br />• Semi-detached house<br />• Town house<br />• Residential apartment<br />• Neighborhood level park and playground<br />• Place of worship or prayer<br />• Place of burial or cremation<br />• Horticultural nursery<br />• Beauty parlor<br />• Urban forest<br />• Non-commercial renewable energy installation",
                PermissibleUses = "• Daycare centre or preschool<br />• Primary school<br />• Secondary school<br />• Dispensary with no bed and laboratory facilities<br />• Library<br />• Post office, fire station, rescue and emergency services<br />• Rehabilitation centre for disabled<br />• Hotel<br />• Indoor sports centre<br />• Private community club<br />• Guest house having not more than ten rooms<br />• Small size corner shop at the ground floor<br />• Polyclinic<br />• Parking plaza<br />• Diplomatic enclave or diplomatic office<br />• Electric vehicle charging centre<br />• Small size corner shop or neighborhood shop up to max 144 sqft on the ground floor<br />• Office of a professional not exceeding 25% of the floor area, as office associated with resident professional",
                Prohibited = "A local government shall not allow a person to use a property in a residential area for a purpose which is neither permitted nor permissible."
            });
        }

        // Check for other categories and add relevant LandUsePlanItems
        if (request.LandUseSearch.Any(item =>
     item.Category.Equals("Commercial", StringComparison.OrdinalIgnoreCase) ||
     item.Category.Equals("Commercial Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0xFF0000";
            landUsePlanItems1.Add(new LandUsePlanItem
            {
                Category = "Commercial Zone (Including Institutional Area)",
                PermittedUses = "• Residential apartment<br />• Multi storey building<br />• Condominium complex<br />• Commercial plaza<br />• Market, shopping mall, departmental store<br />• Business facility<br />• Private office<br />• Government or semi-government office<br />• Court or tribunal<br />• Financial institution<br />• Cultural institution such as park, memorial and monument<br />• Hotel (less than four star)<br />• Motel<br />• Showroom<br />• Shop except dealing with hazardous material<br />• Bakery or confectionary<br />• Beauty Parlour<br />• Hostel<br />• Courier Service<br />• Private telephone exchange<br />• Boutique<br />• Restaurant<br />• Social welfare institution such as community centre, art gallery & museum<br />• Parking plaza<br />• Taxi, rikshaw stand and bus halt<br />• Police station, post office, fire station<br />• Place of worship or prayer",
                PermissibleUses = "• Educational institution<br />• Research institution<br />• Marriage or banquet hall with a minimum plot size of 04 kanals<br />• Cinema<br />• Theater, auditorium, concert hall or exhibition hall with a minimum plot size of 04 kanals<br />• Seasonal commercial fare site<br />• Stadium or play land<br />• Shops dealing in dangerous material<br />• Petrol pump or gas station<br />• Bus or truck terminal<br />• Loading and unloading requirements of all uses<br />• Weighbridge<br />• Private hospital<br />• Hotel four star or above<br />• Television or other studio<br />• Auto workshop<br />• Whole sale storage place<br />• Printing press<br />• Athletic club, gymnasium, fitness centre or indoor sport facility<br />• Base trans receiver station or communication tower<br />• Renewable energy installation",
                Prohibited = "A local government shall not allow a person to use a property in a commercial area for a purpose which is neither permitted nor permissible."
            });
        }

        if (request.LandUseSearch.Any(item =>
    item.Category.Equals("Industrial", StringComparison.OrdinalIgnoreCase) ||
    item.Category.Equals("Industrial Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0x800080";
            landUsePlanItems1.Add(new LandUsePlanItem
            {
                Category = "Industrial Zone",
                PermittedUses = "• Cottage or light industry<br />• Vertical green industry<br />• Construction equipment<br />• Warehouse, storage or distribution centre<br />• Building material store<br />• Fire station<br />• Restaurant<br />• Loading and unloading place<br />• Weighbridge<br />• Industrial units except for which special permission is required<br />• Industrial product display centre<br />• Renewable energy installation<br />• Ancillary office or place for industry<br />• Auto workshop, service garage and service station<br />• Essential residential, commercial and educational facilities for labour/employees<br />• Police station, post office, fire station<br />• Parking lot<br />• Base trans receiver station or communication tower<br />• Water purification plan<br />• Forest or green area<br />• Vocational or training centre<br />• Medium or heavy industry",
                PermissibleUses = "• Storing, packing, pursing, cleaning, preparing, and manufacturing of blasting powder, ammunition, fireworks, gun powder, sulphur, mercury, gases, nitrocompounds, phosphorous, dynamite<br />• Storing explosives, petrol, oil, lubricants, and other inflammable materials including coal, chemicals, liquids or otherwise cleaning, dying, preparing and manufacturing of cloth or yarn in indigo or other colour<br />• Storing, processing, cleaning, crushing, melting, preparing or manufacturing and dealing in bombs, tallow, offal, fat blood, soap, raw or dry hide or skin, washing or dying wool or hair<br />• Casting of heavy metals, electro plating, welding, marble cutting and polishing, manufacturing of cement or pipes, burning or grinding of lime stone, metal or use of any other obnoxious or hazardous material<br />• Petrol pump or gas station or LPG or LNG storage or filling station<br />• Grid station<br />• Power plant<br />• Hospital<br />• Effluent treatment or recycling plant<br />• Industrial units for which special permission is required<br />• Oil depot<br />• Incineration plant<br />• Essential residential, commercial and educational facilities for labour/employees<br />• Place for worship",
                Prohibited = "A local government shall not allow a person to use a property in an industrial area for a purpose which is neither permitted nor permissible."
            });
        }

        if (request.LandUseSearch.Any(item =>
     item.Category.Equals("Agriculture", StringComparison.OrdinalIgnoreCase) ||
     item.Category.Equals("Agriculture Zone", StringComparison.OrdinalIgnoreCase)))
        {
            colorCode = "color:0x00FF00";
            landUsePlanItems1.Add(new LandUsePlanItem
            {
                Category = "Agriculture Zone",
                PermittedUses = "• Crop<br />• Orchard<br />• Pasture land<br />• Livestock such as dairy or poultry farm<br />• Forest<br />• Nursery or a green-house<br />• Tube well<br />• Rural settlement or village<br />• Place of worship or prayer<br />• Place of burial or cremation<br />• Corner shop<br />• Agro-based industry<br />• Cottage industry<br />• Vegetable, fruit and grain market<br />• Cattle market of a local government<br />• Public slaughter house<br />• Gawala colony<br />• Bus or truck terminal and ancillary activities",
                PermissibleUses = "• Agricultural machinery workshop<br />• Brick kiln<br />• Basic health unit<br />• Veterinary dispensary or hospital<br />• Recreational club or resort<br />• Shooting range<br />• Swimming pool<br />• Library<br />• Cold storage<br />• Animal or livestock research institute<br />• Park, monument, playground, gymnasium or sports complex<br />• Vegetable, fruit and grain market<br />• Cattle market of local government<br />• Public toilet<br />• Oil depot<br />• Animal husbandry clinic<br />• Country club<br />• Farm house<br />• Public or private or recreational park<br />• Water filtration plant<br />• Waste water treatment plant<br />• Landfill site<br />• Incineration plant<br />• Birds/wildlife sanctuary<br />• Botanical or zoological garden<br />• Factory outlet<br />• Zoo<br />• Base trans-receiver station or communication tower<br />• Petrol pump or gas station or LPG or LNG storage or filling station",
                Prohibited = "A local government shall not allow a person to use a property in an agriculture area for a purpose which is neither permitted nor permissible."
            });
        }

        // Check each LandUsePlanItem for matches
        foreach (var item in landUsePlanItems1)
        {
            // Check if the searchArea is in the PermittedUses
            if (!string.IsNullOrEmpty(item.PermittedUses) && item.PermittedUses.ToLower().Contains(searchArea.ToLower()))
            {
                isPermitted = true;
            }

            // Check if the searchArea is in the PermissibleUses
            if (!string.IsNullOrEmpty(item.PermissibleUses) && item.PermissibleUses.ToLower().Contains(searchArea.ToLower()))
            {
                isPermissible = true;
            }

            // Check if the searchArea is in the Prohibited uses
            if (!string.IsNullOrEmpty(item.Prohibited) && item.Prohibited.ToLower().Contains(searchArea.ToLower()))
            {
                isProhibited = true;
            }
        }
        string rawMapUrl = null;
        if (!string.IsNullOrEmpty(result.Category) && result.Category.Contains(","))
        {
            rawMapUrl = result.RawMapUrl;
        }
        else
        {
            if (_shapeType == "Point" || _shapeType == "MultiPoint")
            {
                // Generate markers for Point or MultiPoint geometries
                string markers = string.Join("|", coordinates.Select(c => $"{colorCode}|{c[0]},{c[1]}"));
                rawMapUrl = $"https://maps.googleapis.com/maps/api/staticmap?" +
                            $"size=280x230&" +
                            $"maptype=hybrid&" +
                            $"markers={markers}&" +
                            $"key=AIzaSyC9SY7c76e7gZ5qnpo63Rdj-KmyAyQFqHs";
            }
            else
            {
                // Handle shapes like Polygon or LineString with path drawing
                rawMapUrl = $"https://maps.googleapis.com/maps/api/staticmap?" +
                            $"size=280x230&" +
                            $"maptype=hybrid&" +
                            $"path={colorCode}|weight:5|{pathCoordinates}&" +
                            $"key=AIzaSyC9SY7c76e7gZ5qnpo63Rdj-KmyAyQFqHs";
            }
        }

        var htmlContenttd = new StringBuilder();
        htmlContenttd.AppendLine(@"
<td>
    " + permittedpermissibleName + @"<br />
    Status:
    " + (usesResult.Equals("permitted", StringComparison.OrdinalIgnoreCase)
                    ? $"<img src='file:///{checkbox}' alt='checkbox' style='width: 13px; height: 13px; vertical-align: middle;' /> Permitted"
                    : $"<img src='file:///{unchekedimg}' alt='unchecked' style='width: 13px; height: 13px; vertical-align: middle;' /> Permitted") + @"
    " + (usesResult.Equals("permissible", StringComparison.OrdinalIgnoreCase)
                    ? $"<img src='file:///{checkbox}' alt='checkbox' style='width: 13px; height: 13px; vertical-align: middle;' /> Permissible"
                    : $"<img src='file:///{unchekedimg}' alt='unchecked' style='width: 13px; height: 13px; vertical-align: middle;' /> Permissible") + @"
    " + (usesResult.Equals("prohibited", StringComparison.OrdinalIgnoreCase)
                    ? $"<img src='file:///{checkbox}' alt='checkbox' style='width: 13px; height: 13px; vertical-align: middle;' /> Prohibited"
                    : $"<img src='file:///{unchekedimg}' alt='unchecked' style='width: 13px; height: 13px; vertical-align: middle;' /> Prohibited") + @"
</td>");

        var htmlContenttdAbutingRoad = new StringBuilder();

        htmlContenttdAbutingRoad.AppendLine(@"
<td>
    " + (ListARoad != null && !string.IsNullOrEmpty(ListARoad.ToString())
        ? $"<img src='file:///{checkbox}' alt='checkbox' style='width: 13px; height: 13px; vertical-align: middle;' /> List A / Declared Commercial Road ({ListARoad})<br/>"
        : $"<img src='file:///{unchekedimg}' alt='unchecked' style='width: 13px; height: 13px; vertical-align: middle;' /> List A / Declared Commercial Road<br/>") + @"

    
    " + (string.IsNullOrWhiteSpace(ListARoad)
                    ? $"<img src='file:///{checkbox}' alt='unchecked' style='width: 13px; height: 13px; vertical-align: middle;' />"
                    : $"<img src='file:///{unchekedimg}' alt='checkbox' style='width: 13px; height: 13px; vertical-align: middle;' />") +
                    @" Other Road/Area
</td>");

        string landUsePlanHtml = GenerateLandUsePlanHtml(landUsePlanItems1);
        //if (result != null)
        //{
        //    string categories = result.Category; // e.g., "Residential,Commercial"
        //    string areas = result.Area; // e.g., "9.125,4.954"

        //    if (categories.Contains(",") && areas.Contains(","))
        //    {
        //        Zone = string.Empty;
        //        // Split the categories and areas into arrays
        //        string[] categoryArray = categories.Split(',');
        //        string[] areaArray = areas.Split(',');

        //        // Convert areas to double for calculations
        //        double[] areaValues = Array.ConvertAll(areaArray, double.Parse);

        //        // Calculate the total area
        //        double totalArea = areaValues.Sum();

        //        // Build the zone string
        //        for (int i = 0; i < categoryArray.Length; i++)
        //        {
        //            // Calculate the percentage for each area
        //            double percentage = (areaValues[i] / totalArea) * 100;

        //            // Format the output
        //            Zone += $"{categoryArray[i].Trim()} ({areaValues[i]} {percentage:F2}%)";

        //            // Add a comma if it's not the last element
        //            if (i < categoryArray.Length - 1)
        //            {
        //                Zone += ", ";
        //            }
        //        }
        //    }
        //}
        string ConclusionText = "";
      if(usesResult== "prohibited")
        {
            ConclusionText = " under applicable Land Use Rules 2020";
        }
        else
        {
            ConclusionText = " subject to the applicable area/approved development zone and land use";
        }
        string ApplicationOutcome = null;
        if (!string.IsNullOrEmpty(result.Category) && result.Category.Contains(","))
        {
            ApplicationOutcome = $@"
        <h2 class='left-aligned'>APPLICATION OUTCOME</h2>
<div style='display: flex; justify-content: space-between; width: 100%; margin-bottom: 20px;'>
   <div style='width: 65%; float: left;'>
    <table class='left-aligned' style='width: 100%; border-collapse: collapse;'>
      
            <tr><th>Field</th><th>Status</th></tr>
          <tr><td>Approved Development Zone</td><td><b>Category:</b>{Zone}
<b>{subzoneColoumName}</b>{subzone}</td></tr>
         
     <tr>
                <td>Abutting Road</td>
      {htmlContenttdAbutingRoad}
            </tr>
   <tr><td>Proposed Use</td>
{htmlContenttd}

</tr>
            <tr><td>Approval Authority</td><td>DPDC/MC Sahiwal</td></tr>

        </table>
</div>
<div style='width: 100%;float:right;margin-right:100px;'>
    {htmlTable}
</div>
</div>";
        }
        else
        {
            ApplicationOutcome = $@"<h2 class='left-aligned'>APPLICATION OUTCOME</h2>
        <table class='left-aligned'>
            <tr><th>Field</th><th>Status</th></tr>
          <tr><td>Approved Development Zone</td><td><b>Category:</b>{Zone}
<b>{subzoneColoumName}</b>{subzone}</td></tr>
         
     <tr>
                <td>Abutting Road</td>
      {htmlContenttdAbutingRoad}
            </tr>
   <tr><td>Proposed Use</td>
{htmlContenttd}

</tr>
            <tr><td>Approval Authority</td><td>DPDC/MC Sialkot</td></tr>
        </table>
       ";
        }


        string htmlContent = $@"
<html>
    <head>
        <style>
            html, body {{
                font-family: Arial, sans-serif;
                font-size: 12px;
                margin: 0;
                padding: 0;
                position: relative;
            }}
            h1 {{
                text-align: center;
                font-size: 22px;
                font-weight: bold;
                margin: 0; /* Remove default margin */
            }}
            h2 {{
                margin-top: 20px; /* Add some space above h2 */
            }}
            table {{
                width: 100%;
                border-collapse: collapse;
                margin-bottom: 20px;
            }}
            th, td {{
                padding: 8px;
                text-align: left;
                border: 1px solid #ddd; /* This applies to other tables, not the logo table */
                font-size: 12px;
            }}
            th {{
                background-color: #f2f2f2;
            }}
            .logo-table {{
                width: 100%;
               border: none !important; /* Ensure no border */
                margin: 0;
                padding: 0;
                text-align: center;
                border-collapse: collapse; /* Ensure no space between borders */
            }}
            .logo-table td {{
                border: none !important; /* Ensure no border for logo table cells */
                padding: 0;
            }}
            .logo-left {{
                text-align: left;
                padding-left: 10px;
            }}
            .logo-right {{
                text-align: right;
                padding-right: 10px;
            }}
            .address-row td {{
                height: 70px !important;
            }}
            .coloum-row td {{
                height: 30px !important;
            }}
            .field-row td {{
                width: 180px !important;
            }}
            #signature-auth {{
                display: none;
            }}
         .narrow-table {{
                width: 25%;
                margin-top: 20px;
                float: right; /* Align the table to the right side */
            }}
        </style>
    </head>
    <body>
     <table class='logo-table' style='border-collapse: collapse; width: 100%; border: 1px solid white;'>
    <tr>
        <td class='logo-left' style='border: 1px solid white;'>
            <img src='file:///{logo1Path}' alt='Punjab Government Logo' style='width: 100px; height: 85px;' />
        </td>
        <td style='border: 1px solid white; text-align: center;'>
            <h1>LAND USE REPORT</h1>
        </td>
        <td class='logo-right' style='border: 1px solid white;'>
            <img src='file:///{logo2Path}' alt='PICIIP Logo' style='width: 100px; height: 100px;' />
        </td>
    </tr>
</table>
        <h2 class='left-aligned'>APPLICATION DETAILS</h2>

      <table class='left-aligned'>
    <tr>
        <th class='field-row' style='width: 120px;'>Field</th>
        <th style='width: 200px;'>Details</th>
        <th>Location</th>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 100px;'>Applicant Name</td>
        <td>{request.ApplicantName}</td>
        <td rowspan='10'>
Latitude / Longitude: {latitude.ToString()} / {longitude.ToString()}
            <img src='{rawMapUrl}' alt='Map showing location' />
        </td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Applicant CNIC</td>
        <td>{request.ApplicantCNIC}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Application ID</td>
        <td>{request.ApplicationId}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Date of Application</td>
        <td>{request.ApplicationDate}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Contact No.</td>
        <td>{request.ContactDetails}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Email</td>
        <td>{request.Email}</td>
    </tr>
<tr class='coloum-row'>
    <td style='width: 120px;'>Applied Land Use</td>
    <td>
        {(request.LandUseSearch != null && request.LandUseSearch.Count > 0
            ? $"{request.LandUseSearch[0].SearchText} (<b>Category:</b> {request.AppliedLandUse.Replace("Zone", "").Trim()})"
            : "N/A")}
    </td>
</tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Area</td>
        <td>{request.SiteArea}</td>
    </tr>
    <tr class='coloum-row'>
        <td style='width: 120px;'>Abutting Road</td>
        <td>{request.AbutingRoad}</td>
    </tr>
   <tr class='coloum-row'>
        <td style='width: 120px;'>Site Situation</td>
        <td>{request.existingsituation}</td>
    </tr>
</table>
  {ApplicationOutcome}
<h4 style='text-align:left; margin-top: 5px; margin-bottom: 5px;'>Applicable Conversion Fee</h4>
<p style='margin-top: 0; margin-bottom: 5px;'>As per Land Use Rules, 2020 clause 50.</p>

<h4 style='text-align:left; margin-top: 5px; margin-bottom: 5px;'>Conclusion</h4>
<p style='margin-top: 0; margin-bottom: 5px;'>The Applied Land Use is {usesResult} {ConclusionText}.
 {ConverionFeeText}</p>

<h3 style='text-align:center; margin-top: 5px; margin-bottom: 5px;'>DISCLAIMER</h3>
<p style='margin-top: 0; margin-bottom: 5px;'>This is a computer-generated land use report and not valid for Court use.
Approval of Land Use Conversion and Building Plan Approval will be as per applicable
bye-laws and rules</p>

<br/>
<br/>
<table class='logo-table' style='border-collapse: collapse; width: 100%; border: 1px solid white;'>
    <tr>
        <td class='logo-left' style='border: 1px solid white;'>
            <img src='file:///{logo1Path}' alt='Punjab Government Logo' style='width: 100px; height: 85px;' />
        </td>
        <td style='border: 1px solid white; text-align: center;'>
            <h1>LAND USE REPORT</h1>
        </td>
        <td class='logo-right' style='border: 1px solid white;'>
            <img src='file:///{logo2Path}' alt='PICIIP Logo' style='width: 100px; height: 100px;' />
        </td>
    </tr>
</table>
 <h2 style='text-align:left'>ANNEXURE</h2>
 {landUsePlanHtml}
 
    </body>
</html>";


        return htmlContent;
    }

    private List<double[]> ExtractCoordinatesFromGeoJson(string geoJson)
    {
        var coordinates = new List<double[]>();

        // Parse the GeoJSON string to a JObject
        var jsonObject = JObject.Parse(geoJson);
        var features = jsonObject["features"];

        // Loop through each feature to extract coordinates
        foreach (var feature in features)
        {
            var geometry = feature["geometry"];
            if (geometry != null)
            {
                var geometryType = geometry["type"].ToString();
                _shapeType = geometryType; // Assuming you use this elsewhere in your code

                switch (geometryType)
                {
                    case "Point":
                        AddPointCoordinates(coordinates, geometry["coordinates"]);
                        break;

                    case "MultiPoint":
                        foreach (var coord in geometry["coordinates"])
                        {
                            AddPointCoordinates(coordinates, coord);
                        }
                        break;

                    case "LineString":
                        AddLineStringCoordinates(coordinates, geometry["coordinates"]);
                        break;

                    case "Polygon":
                        AddPolygonCoordinates(coordinates, geometry["coordinates"]);
                        break;

                    case "MultiPolygon":
                        foreach (var polygon in geometry["coordinates"])
                        {
                            AddPolygonCoordinates(coordinates, polygon);
                        }
                        break;

                    default:
                        throw new NotSupportedException($"Geometry type '{geometryType}' is not supported.");
                }
            }
        }

        return coordinates;
    }

    // Helper method to add point coordinates
    private void AddPointCoordinates(List<double[]> coordinates, JToken pointCoords)
    {
        coordinates.Add(new double[] { (double)pointCoords[1], (double)pointCoords[0] }); // Latitude, Longitude
    }

    // Helper method to add line string coordinates
    private void AddLineStringCoordinates(List<double[]> coordinates, JToken lineCoords)
    {
        foreach (var coord in lineCoords)
        {
            double lng = (double)coord[0]; // Longitude first
            double lat = (double)coord[1]; // Latitude second
            coordinates.Add(new double[] { lat, lng });
        }
    }

    // Helper method to add polygon coordinates
    private void AddPolygonCoordinates(List<double[]> coordinates, JToken polygonCoords)
    {
        foreach (var ring in polygonCoords)
        {
            // Assuming the first ring is the outer ring
            foreach (var coord in ring)
            {
                double lng = (double)coord[0]; // Longitude first
                double lat = (double)coord[1]; // Latitude second
                coordinates.Add(new double[] { lat, lng });
            }
        }
    }


    private string GenerateLandUsePlanHtml(List<LandUsePlanItem> landUsePlanItems)
    {
        string landUsePlanHtml = "<h2>Punjab Local Government Land Use Plan (Classification, Reclassification, & Redevelopment) Rules 2020</h2>";
        landUsePlanHtml += "<table style='width: 100%; border-collapse: collapse;'><tr><th style='width: 15%;'>Category</th><th style='width: 35%;'>Permitted Uses</th><th style='width: 35%;'>Permissible Uses</th><th style='width: 15%;'>Prohibited</th></tr>";

        foreach (var item in landUsePlanItems)
        {
            landUsePlanHtml += "<tr>";
            landUsePlanHtml += $"<td style='width: 15%;'>{item.Category}</td>";
            landUsePlanHtml += $"<td style='width: 35%;'>{item.PermittedUses}</td>";
            landUsePlanHtml += $"<td style='width: 35%;'>{item.PermissibleUses}</td>";
            landUsePlanHtml += $"<td style='width: 15%;'>{item.Prohibited}</td>";
            landUsePlanHtml += "</tr>";
        }

        landUsePlanHtml += "</table>";
        return landUsePlanHtml;
    }
}
// Method to extract coordinates from GeoJSON

// Model for the request data
public class PdfRequestModel
{
    public string ApplicantName { get; set; }
    public string ApplicantCNIC { get; set; }
    public string ApplicationId { get; set; }
    public string ApplicationDate { get; set; }
    public string ContactDetails { get; set; }
    public string AppliedLandUse { get; set; }
    public string ProposedSite { get; set; }
    public string SiteArea { get; set; }
    public string Email { get; set; }
    public string AbutingRoad { get; set; }
    public string DevelopmentZone { get; set; }
    public string SubZone { get; set; }
    public string ApplicationStatus { get; set; }
    public string DecisionDate { get; set; }
    public string DecisionAuthority { get; set; }
    public string GeoJson { get; set; }
    public List<LandUsePlanItem> LandUsePlanItems { get; set; }
    public List<LandUseSearch> LandUseSearch { get; set; }
    public string ExistingLandtype { get; set; }
    public string NewLandtype { get; set; }
    public string layerName { get; set; }
    public int ObjectId { get; set; }
    public string newSubZoneTable { get; set; }
    public string newSubZoneColoum { get; set; }
    public string parcelAction { get; set; }
    public string request_status { get; set; }
    public bool is_superuser { get; set; }
    public string existingsituation { get; set; }
    public string calltype { get; set; }
    public int plotId { get; set; }
}

// Custom Footer class for the PDF
public class CustomFooter : PdfPageEventHelper
{
    public string FooterText { get; set; }

    public override void OnEndPage(PdfWriter writer, Document document)
    {
        Font footerFont = FontFactory.GetFont(FontFactory.HELVETICA_OBLIQUE, 10);

        // Create the footer text paragraph
        Paragraph footer = new Paragraph(FooterText, footerFont);
        footer.Alignment = Element.ALIGN_CENTER;

        // Create a table for the footer
        PdfPTable footerTable = new PdfPTable(1);
        footerTable.TotalWidth = document.PageSize.Width - document.LeftMargin - document.RightMargin;
        footerTable.AddCell(new PdfPCell(footer)
        {
            Border = PdfPCell.NO_BORDER,
            HorizontalAlignment = Element.ALIGN_CENTER
        });

        // Add the footer table to the document
        footerTable.WriteSelectedRows(0, -1, document.LeftMargin, document.BottomMargin - 10, writer.DirectContent);

        // Add page number as a separate paragraph
        Paragraph pageNumber = new Paragraph($"Page {writer.PageNumber}", footerFont);
        pageNumber.Alignment = Element.ALIGN_RIGHT;

        // Create a new table for page number
        PdfPTable pageNumberTable = new PdfPTable(1);
        pageNumberTable.TotalWidth = document.PageSize.Width - document.LeftMargin - document.RightMargin;
        pageNumberTable.AddCell(new PdfPCell(pageNumber)
        {
            Border = PdfPCell.NO_BORDER,
            HorizontalAlignment = Element.ALIGN_RIGHT
        });

        // Add the page number table to the document
        pageNumberTable.WriteSelectedRows(0, -1, document.LeftMargin, document.BottomMargin - 20, writer.DirectContent);
    }
}

// Model for the Land Use Plan Items
public class LandUsePlanItem
{
    public string Category { get; set; }
    public string PermittedUses { get; set; }
    public string PermissibleUses { get; set; }
    public string Prohibited { get; set; } public string SearchText { get; set; }
}
public class LandUseSearch
{
    public string Category { get; set; }
    public string SearchText { get; set; }
}
public class UpdateLandUseRequest
{
    public string Geometry { get; set; } // Assuming this is a JSON string or similar
    public string LandUseType { get; set; }
    public string ExistingLandUseType { get; set; }
    public string layerName { get; set; }
    public int ObjectId { get; set; }
    public string newSubZoneTable { get; set; }
    public string newSubZoneColoum { get; set; }
    public string parcelAction {  get; set; }
}
