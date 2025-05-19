using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace PICIIPReportAPI
{
    public class Emailservice
    {
        private readonly string _connectionString;

        public Emailservice()
        {
            // Load connection string from web.config
            _connectionString = ConfigurationManager.ConnectionStrings["PostgresConnection"].ConnectionString;
        }
        // Method to generate the Forget Password Email template
        public string GetForgotPasswordEmailTemplate(string username, string email)
        {
            string password="Abc@1234";
          bool result= UpdatePasswordFromDB(username, email);
            if (result)
            {
                return $@"
<html>
<head>
    <style>
        body {{
            font-family: Arial, sans-serif;
            line-height: 1.6;
            color: #333;
        }}
        .container {{
            width: 80%;
            margin: 0 auto;
            padding: 20px;
            border: 1px solid #ddd;
            border-radius: 5px;
            background-color: #f9f9f9;
        }}
        .header {{
            background-color: #4CAF50;
            color: white;
            padding: 10px 0;
            text-align: center;
            border-radius: 5px 5px 0 0;
        }}
        .content {{
            padding: 20px;
            color: #000;
        }}
        .footer {{
            margin-top: 20px;
            padding-top: 10px;
            border-top: 1px solid #ddd;
            text-align: center;
            font-size: 12px;
            color: #777;
        }}
        .disclaimer {{
            margin-top: 20px;
            font-size: 12px;
            color: #777;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Forgot Password Request</h1>
        </div>
        <div class='content'>
            <p>Dear {username},</p>
            <p>Your password for the system has been reset. Please find your credentials below:</p>
            <p><strong>Password:</strong> {password}</p>
            <p>If you did not request this password reset, please ignore this email.</p>
            <p>For further assistance, please contact our support team.</p>
        </div>
        <div class='footer'>
            &copy; {DateTime.Now.Year} DECISION SUPPORT SYSTEM. All rights reserved.
        </div>
        <div class='disclaimer'>
            <p><strong>DISCLAIMER:</strong> This is a computer-generated land use report and not valid for Court use. Approval of Land Use Conversion and Building Plan
Approval will be as per applicable bye-laws and rule.</p>
        </div>
    </div>
</body>
</html>";
            }
            else return null;
        }
        public string GetApprovalNotificationEmailTemplate(string username, string email)
        {
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(email))
            {
                return $@"
<html>
<head>
    <style>
        body {{
            font-family: Arial, sans-serif;
            line-height: 1.6;
            color: #333;
        }}
        .container {{
            width: 80%;
            margin: 0 auto;
            padding: 20px;
            border: 1px solid #ddd;
            border-radius: 5px;
            background-color: #f9f9f9;
        }}
        .header {{
            background-color: #007BFF;
            color: white;
            padding: 10px 0;
            text-align: center;
            border-radius: 5px 5px 0 0;
        }}
        .content {{
            padding: 20px;
            color: #000;
        }}
        .footer {{
            margin-top: 20px;
            padding-top: 10px;
            border-top: 1px solid #ddd;
            text-align: center;
            font-size: 12px;
            color: #777;
        }}
        .disclaimer {{
            margin-top: 20px;
            font-size: 12px;
            color: #777;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Approval Notification</h1>
        </div>
        <div class='content'>
            <p>Dear {username},</p>
            <p>We are pleased to inform you that your request has been approved.</p>
            <p>Please log in to the portal to check the details and download the report.</p>
            <p>If you have any questions or need further assistance, please contact our support team.</p>
        </div>
        <div class='footer'>
            &copy; {DateTime.Now.Year} DECISION SUPPORT SYSTEM. All rights reserved.
        </div>
        <div class='disclaimer'>
            <p><strong>DISCLAIMER:</strong> This is a computer-generated land use report and not valid for Court use. Approval of Land Use Conversion and Building Plan
Approval will be as per applicable bye-laws and rule.</p>
        </div>
    </div>
</body>
</html>";
            }
            else
            {
                return null;
            }
        }

        public bool UpdatePasswordFromDB(string username, string email)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                try
                {
                    connection.Open();
                    using (var command = new NpgsqlCommand(@"
                UPDATE public.auth_user
                SET password = 'pbkdf2_sha256$870000$4IAuohaiv9iHtCFh4yQWDl$s6umeYdHny+hSjo0DHTVV/mli+PqVbFdRnK5tVI8m1U='
                WHERE username = @username AND email = @email;", connection))
                    {
                        command.Parameters.AddWithValue("username", username);
                        command.Parameters.AddWithValue("email", email);

                        int rowsAffected = command.ExecuteNonQuery();
                        return rowsAffected > 0; // Returns true if the update was successful
                    }
                }
                catch (Exception ex)
                {
                    // Log the exception and return false
                    Console.WriteLine($"Error updating password: {ex.Message}");
                    // Optionally log ex.StackTrace for more details
                    return false;
                }
            }
        }
    }
}
