using System;
using System.Configuration;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Npgsql;

namespace PICIIPReportAPI.Controllers
{
    public class AccountController : ApiController
    {
        private readonly string _connectionString;

        public AccountController()
        {
            // Load connection string from web.config
            _connectionString = ConfigurationManager.ConnectionStrings["PostgresConnection"].ConnectionString;
        }

        // POST: api/account/login
        [HttpPost]
        [Route("api/account/login")]
        public IHttpActionResult AuthenticateUser([FromBody] UserLoginModel loginModel)
        {
            if (loginModel == null || string.IsNullOrEmpty(loginModel.Username) || string.IsNullOrEmpty(loginModel.Password) || string.IsNullOrEmpty(loginModel.CityName))
            {
                return BadRequest("Username, password, and city name are required.");
            }

            // Query the PostgreSQL database to verify the user with username, password, and city name
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                connection.Open();

                // Prepare the SQL query to check if the user exists with provided username, password, and city name
                string query = "SELECT city_name FROM auth_user WHERE username = @username  AND city_name = @city_name";

                using (var command = new NpgsqlCommand(query, connection))
                {
                    // Add parameters to prevent SQL injection
                    command.Parameters.AddWithValue("@username", loginModel.Username);
                    command.Parameters.AddWithValue("@city_name", loginModel.CityName);

                    // Execute the query and get the result
                    var result = command.ExecuteScalar();

                    if (result != null)
                    {
                        // Return success and city_name if credentials match
                        return Ok(new { city_name = result.ToString(), success = true });
                    }
                    else
                    {
                        return Ok(new { city_name = "", success = true });
                    }
                }
            }
        }
    }

    // Define the model to capture the login details from the request body
    public class UserLoginModel
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string CityName { get; set; }  // Added city_name field
    }
}
