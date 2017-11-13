﻿using Newtonsoft.Json.Linq;
using Quartz;
using System;
using System.IO;
using System.Net;
using System.Text;
using Twilio;
using Twilio.Http;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using TwilioConference.DataServices;

namespace TwilioConference.Timer
{
    public class MessageJob : IJob
    {
  

        public void Execute(IJobExecutionContext context)
        {
            TwilioConferenceServices conferenceServices = new TwilioConferenceServices(true);
            JobDataMap dataMap = context.JobDetail.JobDataMap;

            string twilloAccountSid = dataMap.GetString("twilloAccountSid");
            string twilloAccountToken = dataMap.GetString("twilloAccountToken");
            string callSid = dataMap.GetString("callSid");
            int id = dataMap.GetInt("id");
            string conferenceName = dataMap.GetString("conferenceName");
            string dataMapValues =
                string.Format("1. {0}|2. {1}|3. {2}|4. {3}|5. {4}|"
               , twilloAccountSid
               , twilloAccountToken
               , callSid
               , id
               , conferenceName);
            conferenceServices.LogMessage(dataMapValues, id);

            //TwilioClient.Init(twilloAccountSid, twilloAccountToken);
            //TwilioClient.Init("ACb8962de6df9de99d4a711879eccd0cab", "8b60eca3f36b9cfdf4ba9c6ef1877a8c");
            string connectUrl = "http://callingserviceconference.azurewebsites.net/twilioconference/ConnectTwilioBot";
            try
            {

                string postUrl =
                    string.Format("https://api.twilio.com/2010-04-01/Accounts/{0}/Calls.json", twilloAccountSid);
                WebRequest myReq = WebRequest.Create(postUrl);
                string credentials = string.Format("{0}:{1}", twilloAccountSid, twilloAccountToken);
                CredentialCache mycache = new CredentialCache();
                myReq.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
                string formencodeddata = string.Format("To=+1{0}&From=+1{1}&Url={2}"
                    , Constants.TWILIO_CONFERENCE_NUMBER
                    , "4159186649"
                    , connectUrl);
                byte[] formbytes = System.Text.ASCIIEncoding.Default.GetBytes(formencodeddata);
                myReq.Method = "POST";
                myReq.ContentType = "application/x-www-form-urlencoded";
                //conferenceServices.LogMessage("credentials " + credentials.ToString(), id);
                //conferenceServices.LogMessage("get request stream " + myReq.GetRequestStream(), id);
                //conferenceServices.LogMessage("formencodeddata " + formencodeddata.ToString(), id);
                using (Stream postStream = myReq.GetRequestStream())
                {
                    postStream.Write(formbytes, 0, formbytes.Length);
                }

                WebResponse wr = myReq.GetResponse();
                Stream receiveStream = wr.GetResponseStream();
                StreamReader reader = new StreamReader(receiveStream, Encoding.UTF8);
                string content = reader.ReadToEnd();
                //conferenceServices.LogMessage(content, id);
                reader.Close();
                reader.Dispose();
                wr.Close();
                wr.Dispose();

                //CallResource.CreateAsync(
                //    to: new PhoneNumber(Constants.TWILIO_CONFERENCE_NUMBER)
                //    , from: new PhoneNumber("4159656328")
                //    , url: new Uri(connectUrl)
                //    , method: HttpMethod.Post).Wait();
            }
            catch (Exception ex)
            {
                conferenceServices.LogMessage("On MessageJob Error: " + ex.Message +" "+ ex.Source.ToString() + " "+ex.InnerException.Message, id);                
            }

        }
    }
}
