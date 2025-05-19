using PICIIPReportAPI;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Web.Http;

public class EmailController : ApiController
{
    private readonly string _smtpHost = "smtp.gmail.com";
    private readonly int _smtpPort = 587;
    private readonly string _smtpUser = "infopiciipdss@gmail.com";
    private readonly string _smtpPassword = "goprukhkiywartaw";

    [HttpPost]
    [Route("api/email/send")]
    public async Task<IHttpActionResult> SendEmail(EmailRequest emailRequest)
    {
        try
        {
            Emailservice emailservice = new Emailservice();
            // Generate the forgot password email body content using the template
            string bodyContent = emailservice.GetForgotPasswordEmailTemplate(emailRequest.username, emailRequest.Email);
            if (bodyContent == null) { return Ok(new { Success = false }); }
            else { 
            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress("infopiciipdss@gmail.com", "System Notifications");
                msg.To.Add(emailRequest.To);
                if (!string.IsNullOrEmpty(emailRequest.Cc))
                {
                    msg.CC.Add(emailRequest.Cc);
                }
                msg.Subject = emailRequest.Subject;
                msg.Body = bodyContent;
                msg.IsBodyHtml = true;

                using (var smtp = new SmtpClient(_smtpHost, _smtpPort))
                {
                    smtp.Credentials = new NetworkCredential(_smtpUser, _smtpPassword);
                    smtp.EnableSsl = true;
                    await smtp.SendMailAsync(msg);
                }
            }
            return Ok(new { Success = true });
        }
        }
        catch (Exception ex)
        {
            return InternalServerError(ex);
        }
    }

    [HttpPost]
    [Route("api/email/sendApprovalNotification")]
    public async Task<IHttpActionResult> sendApprovalNotification(EmailRequest emailRequest)
    {
        try
        {
            Emailservice emailservice = new Emailservice();
            // Generate the forgot password email body content using the template
            string bodyContent = emailservice.GetApprovalNotificationEmailTemplate(emailRequest.username, emailRequest.Email);
            if (bodyContent == null) { return Ok(new { Success = false }); }
            else
            {
                using (var msg = new MailMessage())
                {
                    msg.From = new MailAddress("infopiciipdss@gmail.com", "Approval Notifications");
                    msg.To.Add(emailRequest.To);
                    if (!string.IsNullOrEmpty(emailRequest.Cc))
                    {
                        msg.CC.Add(emailRequest.Cc);
                    }
                    msg.Subject = emailRequest.Subject;
                    msg.Body = bodyContent;
                    msg.IsBodyHtml = true;

                    using (var smtp = new SmtpClient(_smtpHost, _smtpPort))
                    {
                        smtp.Credentials = new NetworkCredential(_smtpUser, _smtpPassword);
                        smtp.EnableSsl = true;
                        await smtp.SendMailAsync(msg);
                    }
                }
                return Ok(new { Success = true });
            }
        }
        catch (Exception ex)
        {
            return InternalServerError(ex);
        }
    }
}

public class EmailRequest
{
    public string Subject { get; set; }
    public string To { get; set; }
    public string Cc { get; set; }
    public string BodyContent { get; set; }

    public string Email { get; set; }
    public string username { get; set; }
}
