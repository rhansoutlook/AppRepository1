﻿using System;
using System.Configuration;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Mvc;
using Twilio;
using Twilio.AspNet.Common;
using Twilio.AspNet.Mvc;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Rest.Api.V2010.Account.Conference;
using Twilio.TwiML;
using Twilio.Types;
using TwilioConference.DataServices;
using TwilioConference.DataServices.Entities;
using TwilioConference.Models;
using NodaTime;
using Twilio.TwiML.Voice;

namespace TwilioConference.Controllers
{
    public class TwilioConferenceController : TwilioController
    {
        #region Variable Declarations
        // For API authorization
        static readonly string TWILIO_ACCOUNT_SID = ConfigurationManager.AppSettings["TWILIO_ACCOUNT_SID"];
        static readonly string TWILIO_ACCOUNT_TOKEN = ConfigurationManager.AppSettings["TWILIO_ACCOUNT_TOKEN"];
        static readonly int TIMEOUT_INTERVAL = 30;

        // 1. This is the TWILIO phone number used as a BOT to play the message
        // Obtained from web config
        static readonly string TWILIO_BOT_NUMBER = ConfigurationManager.AppSettings["TWILIO_BOT_NUMBER"];

        // 2. This is the Twiio number that has been used to initiate the conference
        // Captured on call pickup and then stored to conference table
        //string SERVICE_USER_TWILIO_PHONE_NUMBER = "";

        // 3. This is the Number of the registered user on which the conference call will be received
        // Obtained from user table based on 2 above
        string SERVICE_USER_CONFERENCE_WITH_NUMBER = "";

        static readonly string WEBROOT_PATH = System.Reflection.Assembly.GetExecutingAssembly().CodeBase;
        static readonly string WEB_BIN_ROOT = System.IO.Path.GetDirectoryName(WEBROOT_PATH);
        // Do not change path
        static readonly string WEB_JOBS_DIRECTORY = System.IO.Path.GetFullPath("D:\\home\\site\\wwwroot\\app_data\\jobs\\triggered\\TwilioConferenceTimer\\Webjob");
        //To get the location the assembly normally resides on disk or the install directory
        static readonly string TIMER_EXE = Path.Combine(WEB_JOBS_DIRECTORY, "TwilioConference.Timer.exe");

        const string strUTCTimeZoneID = "Etc/UTC";
        static string strTargetTimeZoneID = "";
        
        Boolean AVAILABILITY_CHECK_DONE = false;
        Boolean IsUserAvailableToTakeCalls = true;

        private string strCallServiceUserName = "";
        private CallResource _crCurrentCall;
        private ConferenceResource _crCurrentConference;
        private string _strCallSID;
        private string _strConferenceSID;
        double messageIntervalinSeconds;
        double hangupIntervalinSeconds;
        double warningIntervalinSeconds;

        private TwilioConferenceServices conferenceServices = new TwilioConferenceServices();
        private bool UserConnected;

        #endregion

        #region Constructor

        public TwilioConferenceController() 
        {
            //conferenceServices.LogMessage("In contructor");
            TwilioClient.Init(TWILIO_ACCOUNT_SID, TWILIO_ACCOUNT_TOKEN);
        }

        #endregion

        #region Web Methods
 
        [System.Web.Mvc.HttpPost]
        public ActionResult Connect(VoiceRequest request)
        {
            // Step 1  Determine if User is available
            //         Update User Availability Check flag - to prevent checking again when 
            //         this routine is called by Twilio_message_number
            // Step 2  If user is not available, then play error message
            //         control flow should go out of this method i.e. return
            // Step 3  Assuming that user is available, then 
            //         check call start time
            //         If Call start time is within defined parameters
            //         then put connect the conference
            //         Else Pause for calculated period of time
            // Step 4  Check if incoming call is from TWILIO_MESSAGE_PHONE
            //         if Yes, Dial into conferencename
            //         if No, then get a new conference name and dial into conference

             string SERVICE_USER_TWILIO_PHONE_NUMBER = "";
            //conferenceServices.LogMessage("In connect");
            var starttime = DateTime.Now;
            DateTime stoptime;
            var elapsedtime = 0;

            var response = new VoiceResponse();
            string fromPhoneNumber = request.From;
            SERVICE_USER_TWILIO_PHONE_NUMBER = request.To;

            if (fromPhoneNumber.Contains(TWILIO_BOT_NUMBER))
            {
                // If Dial in from BOT then connect to existing conference with message or warning
                response = ConnectBotToExistingConference(SERVICE_USER_TWILIO_PHONE_NUMBER);
                return new TwiMLResult(response);
            }

            
            if (!AVAILABILITY_CHECK_DONE)
            {
                try
                {
                    //TwilioClient.Init(TWILIO_ACCOUNT_SID, TWILIO_ACCOUNT_TOKEN);

                    // Step 1
                    IsUserAvailableToTakeCalls = conferenceServices.
                        CheckAvailabilityAndFetchDetails(
                        SERVICE_USER_TWILIO_PHONE_NUMBER.Substring(2),
                        ref strCallServiceUserName,
                        ref SERVICE_USER_CONFERENCE_WITH_NUMBER,
                        ref strTargetTimeZoneID);

                    
                    if (!IsUserAvailableToTakeCalls)
                    {
                        response.Say(string.Concat(strCallServiceUserName,
                                      "is not available to take calls at this time. ",
                                       "Please check back with this user about their available call times"));
                        response.Hangup();
                        return TwiML(response);
                    }

                    conferenceServices.LogMessage(string.Concat("Step 1 Availability Check ",
                        string.Format("|Twilio Phone Number {0}" +
                                        "| User Name {1} " +
                                        "|Conference Number {2}" +
                                        "| Time Zone {3}" +
                                        "| Is Available? {4}|"
                                        ,SERVICE_USER_TWILIO_PHONE_NUMBER
                                        ,strCallServiceUserName
                                        ,SERVICE_USER_CONFERENCE_WITH_NUMBER
                                        ,strTargetTimeZoneID
                                        ,IsUserAvailableToTakeCalls)),1,0);
                }
                catch (Exception ex)
                {
                    conferenceServices.ErrorMessage(string.Format("|Error Message - {0}| 1.Source {1} | 2.Trace {2} |3.Inner Exception {3} |",
                       ex.Message,
                       ex.Source,
                       ex.StackTrace,
                       ex.InnerException));
                }
            }


            #region For debugging

            //var req = Request.InputStream;
            //var json = new StreamReader(req).ReadToEnd();
            //conferenceServices.LogMessage("Json " + json);

            #endregion


            ZonedDateTime utcCallStartTime, targetCallStartTime;
            DateTimeZone tzTargetDateTime;

            try
            {
                CalculateCallStartTimeValues(out utcCallStartTime, out tzTargetDateTime, out targetCallStartTime);

                int intMinutesToPause = 0;
                int intSecondsToPause = 0;
                var strHourMessage = string.Empty;

                Boolean callStartAtTimeSlot =
                    CallStartAtTimeSlot(
                      targetCallStartTime.LocalDateTime.Minute,
                      targetCallStartTime.LocalDateTime.Second,
                      ref intMinutesToPause,
                      ref intSecondsToPause,
                      ref strHourMessage,
                      ref messageIntervalinSeconds,
                      ref hangupIntervalinSeconds,
                      ref warningIntervalinSeconds);

                response.Say(string.Format("You've reached the line of {0} ", strCallServiceUserName));
                response.Pause(1);

                    if (!callStartAtTimeSlot)
                    {
                        response.Say("You will be connected in " + intMinutesToPause.ToString());
                        response.Say("minutes and " + intSecondsToPause.ToString() + " seconds " + strHourMessage);
                        response.Say("Please hold ");
                        response.Pause(1);

                    stoptime = DateTime.Now;
                    elapsedtime = (stoptime - starttime).Seconds;
                    //conferenceServices.LogMessage(string.Format("stoptime {0} startime {1} elapsedtime {2} ", stoptime, starttime, elapsedtime));
                    // PRODUCTION CHANGE  (Uncomment line below)
                    response.Pause(((intMinutesToPause * 60) + intSecondsToPause) - (elapsedtime + 14));

                    #region Experimental Code
                    // This snippet of code was originally intended to list out multiple conferences from Twilio
                    // To test out the code only when possible
                    //ReadConferenceOptions rco = new ReadConferenceOptions();
                    //rco.Status = ConferenceResource.StatusEnum.InProgress;
                    //var conferences = ConferenceResource.Read(rco);

                    //foreach (var conference in conferences)
                    //{
                    //    conference.Uri.ToString();
                    //}

                    #endregion
                }

                    // This is phone of the person that calls the twilo number
                    string phoneFrom = fromPhoneNumber;

                    // Number on which  Service User prefers to take calls - Get from DB     
                    string phoneTo = string.Format("+{0}", SERVICE_USER_CONFERENCE_WITH_NUMBER);

                    // Get a random conference name
                    string conferenceName = conferenceServices.
                                            GetRandomConferenceName();

                    TwilioConferenceCall conferenceRecord =
                        conferenceServices
                        .CreateTwilioConferenceRecord
                          (phoneFrom, phoneTo, SERVICE_USER_TWILIO_PHONE_NUMBER, conferenceName, request.CallSid,
                          hangupIntervalinSeconds, messageIntervalinSeconds, warningIntervalinSeconds, ToDateTime(targetCallStartTime));

                    conferenceServices.LogMessage(string.Concat("|Step 2 Connecting Caller |",
                        string.Format("|Connect From {0} " +
                                   "|Connect To {1}"  +
                                   "|Conference Number {2}" +
                                   "|Call Start Time Meet Req {3}" +
                                   "|Conference Name {4} " + 
                                   "|Call SID {5}" +
                                   "|User NAme {6}" +
                                   "|targetCallStartTime {7}" +
                                   "|targetCallStartTime.LocalDateTime.Minute {8}" +
                                   "|targetCallStartTime.LocalDateTime.Second {9}" +
                                   "|utcCallStartTime {10}" +
                                   "|tzTargetDateTime {11}" +
                                   "|Minutes to pause {12}|" +
                                   "|Seconds to pause {13}|" +
                                   "|messageIntervalinSeconds {14} |" +
                                   "|hangupIntervalinSeconds {15}| " +
                                   "|warningIntervalinSeconds {16}| " +
                                   "|elapsedtime {17}| "
                                   , phoneFrom
                                   , phoneTo
                                   , SERVICE_USER_CONFERENCE_WITH_NUMBER
                                   , callStartAtTimeSlot
                                   , conferenceName
                                   , request.CallSid
                                   , strCallServiceUserName
                                   , targetCallStartTime
                                   , targetCallStartTime.LocalDateTime.Minute
                                   , targetCallStartTime.LocalDateTime.Second
                                   , utcCallStartTime
                                   , tzTargetDateTime
                                   , intMinutesToPause
                                   , intSecondsToPause
                                   , messageIntervalinSeconds
                                   , hangupIntervalinSeconds
                                   , warningIntervalinSeconds
                                   , elapsedtime  
                                   ))
                                   ,2, 
                                   conferenceRecord.Id);

                response.Say("Connecting Now");
                var dial = new Dial();

                var statusCallbackEventlist = new List<Conference.EventEnum>()
                {
                    Conference.EventEnum.Start,
                    Conference.EventEnum.End,
                    Conference.EventEnum.Join,
                    Conference.EventEnum.Leave
                };
                UserConnected = false;
                dial.TimeLimit = 600;  // Set total time limit of call
                dial.Timeout = TIMEOUT_INTERVAL;     // Set total timeout for call (System hangs up after time limit)
                dial.Conference(
                    name: conferenceName                                                                               
                    , waitUrl: new Uri("https://callingserviceproduction.azurewebsites.net//twilioconference/ReturnHoldMusicURI")
                    , statusCallbackEvent: statusCallbackEventlist
                    , statusCallback: new Uri(string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/HandleConferenceStatusCallback?id={0}", conferenceRecord.Id))
                    , statusCallbackMethod: Twilio.Http.HttpMethod.Post
                    , startConferenceOnEnter: true
                    , beep: Conference.BeepEnum.Onenter                                     
                    , endConferenceOnExit: true);

                response.Append(dial);

                #region Experimental Code
                //if (!secondParticipantConnected)
                //{
                //    conferenceServices.LogMessage("Participant is not connected");
                //    ConferenceResource.Update(conferenceRecord.ConferenceSID, TWILIO_ACCOUNT_SID, ConferenceResource.UpdateStatusEnum.Completed);
                //}
                //else
                //    conferenceServices.LogMessage("Participant is connected");

                if (!UserConnected)
                {
                    //conferenceServices.LogMessage("Participant is not connected");
                    //response.Pause(3);
                    //var confHangup = new Hangup();
                    //conferenceServices.LogMessage("Should hang up now");
                    //response.Hangup();
                    //ConferenceResource.Update(conferenceRecord.ConferenceSID, TWILIO_ACCOUNT_SID, ConferenceResource.UpdateStatusEnum.Completed);
                }

                #endregion

            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format("|Error Message - {0}| 1.Source {1} | 2.Trace {2} |3.Inner Exception {3} |",
                   ex.Message,
                   ex.Source,
                   ex.StackTrace,
                   ex.InnerException));
                throw;                
            }

            return TwiML(response);
        }

        private VoiceResponse ConnectBotToExistingConference(string SERVICE_USER_TWILIO_PHONE_NUMBER)
        {
            string conferenceName = "";
            var conferenceid = 0;
            var response = new VoiceResponse();
            try
            {
                conferenceName = conferenceServices
                                .GetMostRecentConferenceNameFromNumber
                                (ref conferenceid,
                                SERVICE_USER_TWILIO_PHONE_NUMBER);

                conferenceServices.LogMessage(string.Concat("Connecting Bot to conference ",
                                                 string.Format("|Twilio Bot Number {0}" +
                                                               "|Conference Name {1}" +
                                                               "|Twilio Phone number {2}" 
                                                               ,TWILIO_BOT_NUMBER
                                                               ,conferenceName
                                                               ,SERVICE_USER_TWILIO_PHONE_NUMBER))
                                                               ,0,conferenceid);

                var dial = new Dial();
                var x = dial.Conference(conferenceName,
                        beep: Conference.BeepEnum.True);
                response.Append(dial);

            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
            }

            return response;
        }

        [System.Web.Mvc.HttpPost]
        public TwiMLResult HandleConferenceStatusCallback(TConferenceRequest request)
        {
            //Uncomment below to print full
            //request body
            //var req = Request.InputStream;
            //var json = new StreamReader(req).ReadToEnd();
            //conferenceServices.LogMessage(json);

            var response = new VoiceResponse();

            string conferenceRecordId = Request.QueryString["id"];

            int id = Int32.Parse(conferenceRecordId);

            TwilioConferenceCall conferenceRecord = conferenceServices.GetConferenceRecord(id);

            var conferenceCallBackEvent = request.StatusCallbackEvent;

            switch (conferenceCallBackEvent)
            {
                case "participant-join":
                    if (request.CallSid == conferenceRecord.PhoneCall1SID)
                    {
                        conferenceRecord.ConferenceSID = request.ConferenceSid;
                        conferenceServices.UpdateConference(conferenceRecord);
                        var Call2SID = "";
                        conferenceServices.LogMessage(string.Format("Step 3 Dialing Participant " +
                            "|conferenceRecord.PhoneTo {0}" +
                            "|conferenceRecord.TwilioPhoneNumber {1}" +
                            "|conferenceRecord.ConferenceName {2}" +
                            "|conferenceRecord.ConferenceSID {3}",
                            conferenceRecord.PhoneTo,
                            conferenceRecord.TwilioPhoneNumber,
                            conferenceRecord.ConferenceName,
                            request.ConferenceSid), 3, id);

                        Call2SID = ConnectParticipant(conferenceRecord.PhoneTo, conferenceRecord.TwilioPhoneNumber, conferenceRecord.ConferenceName, request.ConferenceSid, id);
                        conferenceServices.UpdateConferenceCall2SID(id, Call2SID);
                    }
                    break;
                case "participant-leave":
                    if ((request.CallSid == conferenceRecord.PhoneCall1SID)   // If either of the two participants leave the confernce 
                        || (request.CallSid == conferenceRecord.PhoneCall2SID))
                    {

                        var conf = ConferenceResource.Fetch(request.ConferenceSid);
                        conferenceServices.LogMessage(string.Format("Step 10 Participant leave  Call SID {0} conf status {1}", request.CallSid, conf.Status.ToString()), 10, id);
                        // The following lines get executed when the user terminates the call prematurely
                        // Note: It is not possible to trap when the call ends prematurely by the caller terminating the call
                        if (conf.Status == ConferenceResource.StatusEnum.InProgress)
                        {
                            conferenceServices.LogMessage("This call has been ended prematurely", 0, id);
                            // Provide a message to caller before disconnecting the conference (TODO)                            
                            
                            // Provide a message
                            // TODO 
                            // (Use Timer.exe to disconnect call)
                            // Disconnect the call
                            ConferenceResource.Update(request.ConferenceSid, status: ConferenceResource.UpdateStatusEnum.Completed);
                        }
                    }
                    break;
                case "conference-start":
                    {
                        #region Keep these two lines
                        //conferenceRecord.ConferenceSID = request.ConferenceSid;
                        //conferenceServices.UpdateConference(conferenceRecord);

                        #endregion                        try

                        #region Experimental Code
                        // Find number of participants in Conference
                        // If only 1 partiicpant
                        // Shut down conference
                        // Only 1 participant implies that caller could not be connected to user
                        //var conferences = ConferenceResource.Read(pathAccountSid:null,
                        //                        dateCreatedBefore:null,dateCreated:null,dateCreatedAfter:null,dateUpdatedBefore:null,dateUpdated:null,dateUpdatedAfter:null,friendlyName: conferenceRecord.ConferenceName);
                        ////conferences.GetEnumerator()

                        //var conferenceList = new List<ConferenceResource>();
                        //var confEnum = conferences.GetEnumerator();
                        //while (confEnum.MoveNext())
                        //    conferenceList.Add(confEnum.Current);

                        //conferenceServices.LogMessage("No of participants " + conferenceList.Count.ToString());
                        //foreach (var conference in conferenceList)
                        //{
                        //    conferenceServices.LogMessage(string.Format("conference.AccountSid {0} conference.DateCreated {1} conference.FriendlyName {2} conference.Sid{3} conference.Status {4}", conference.AccountSid, conference.DateCreated, conference.FriendlyName, conference.Sid, conference.Status), 0, id);

                        //}

                        //conferenceServices.LogMessage("No of participants " + participantList.Count.ToString());
                        //foreach (var participant in participantList)
                        //{
                        //    conferenceServices.LogMessage(string.Format(" participant.AccountSid {0} participant.DateCreated {1} participant.CallSid {2} participant.ConferenceSid {3} participant.Status {4} ", participant.AccountSid, participant.DateCreated, participant.CallSid, participant.ConferenceSid, participant.Status), 0, id);
                        //}


                        //if (!secondParticipantConnected)
                        //{
                        //    //conferenceServices.LogMessage("Participant is not connected");
                        //    //response.Pause(3);
                        //    //var confHangup = new Hangup();
                        //    //response.Hangup();
                        //    //ConferenceResource.Update(conferenceRecord.ConferenceSID, TWILIO_ACCOUNT_SID, ConferenceResource.UpdateStatusEnum.Completed);
                        //}
                        //else
                        //    //conferenceServices.LogMessage("Participant is connected");

                        #endregion

                        
                        try
                        {
                            UserConnected = CheckIfUserConnectedSuccessfully(request, conferenceRecord);
                            ZonedDateTime utcConferenceStartTime, targetConferenceStartTime;
                            DateTimeZone tzTargetDateTime;
                            if (UserConnected)
                            {
                                CalculateConferenceStartTimeValues(out utcConferenceStartTime, out tzTargetDateTime, out targetConferenceStartTime);
                                conferenceServices.UpdateConferenceStartTime(id, ToDateTime(targetConferenceStartTime));
                            }

                            conferenceServices.LogMessage(string.Format("Step 7 Starting Scheduled Timer|",
                                                                        "|Exe Name & Path {0}" +
                                                                        "|Conference ID {1}" +
                                                                        "|TwilioPhone  {2}" +
                                                                        "|Call SID  {3} |" +
                                                                        "|messageIntervalinSeconds {4}|" +
                                                                        "|hangupIntervalinSeconds {5}|" +
                                                                        "|warningIntervalinSeconds {6}|" +
                                                                        "|request.Timestamp.Kind {7}|"
                                                                        , TIMER_EXE
                                                                        , conferenceRecordId
                                                                        , conferenceRecord.TwilioPhoneNumber
                                                                        , request.CallSid
                                                                        , conferenceRecord.messageIntervalInSeconds
                                                                        , conferenceRecord.hangupIntervalInSeconds
                                                                        , conferenceRecord.warningIntervalInSeconds
                                                                        , request.Timestamp.Kind), 7, id);


                            string[] args = { UserConnected.ToString(),
                                                  conferenceRecordId.ToString(),
                                                  conferenceRecord.TwilioPhoneNumber,
                                                  conferenceRecord.messageIntervalInSeconds.ToString(),
                                                  conferenceRecord.warningIntervalInSeconds.ToString(),
                                                  conferenceRecord.hangupIntervalInSeconds.ToString() };

                            ProcessStartInfo processStartInfo = new ProcessStartInfo();
                            processStartInfo.FileName = TIMER_EXE;
                            processStartInfo.Arguments = string.Join(" ", args);
                            Process.Start(processStartInfo);
                        }
                        catch (Exception ex)
                        {
                            conferenceServices.ErrorMessage(string.Format(
                                               "|Error Message - {0}" +
                                               "| 1.Source {1} " +
                                               "| 2.Trace {2} " +
                                               "| 3.Inner Exception {3} |",
                                               ex.Message,
                                               ex.Source,
                                               ex.StackTrace,
                                               ex.InnerException));
                        }
                    }
                    break;
                case "conference-end":
                       var conf1 = ConferenceResource.Fetch(request.ConferenceSid);
                    try
                    {
                        conferenceServices.UpdateActiveStatus(id, false);
                        var processArray = Process.GetProcessesByName("TwilioConference.Timer");
                        foreach (var p in processArray)
                        {
                            if (p.ProcessName == "TwilioConference.Timer")
                            {
                                p.Kill();
                                p.Close();
                                p.Dispose();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        conferenceServices.ErrorMessage(string.Format(
                                           "|Error Message - {0}" +
                                           "| 1.Source {1} " +
                                           "| 2.Trace {2} " +
                                           "| 3.Inner Exception {3} |",
                                           ex.Message,
                                           ex.Source,
                                           ex.StackTrace,
                                           ex.InnerException));
                        throw;
                    }
                    finally
                    {
                        conferenceServices.LogMessage("Step 10 Conference is " + conf1.Status, 10, id);
                    }
                    break;
            }
            
           // conferenceServices.LogMessage(string.Format("Conference Status {0} - {1} - {2}", conferenceCallBackEvent, request.ConferenceSid, request.CallSid), 0,id);
            return new TwiMLResult(response);
        }

        private bool CheckIfUserConnectedSuccessfully(TConferenceRequest request, TwilioConferenceCall conferenceRecord)
        {
            var retVal = false;
            var participants = ParticipantResource.Read(request.ConferenceSid);
            var participantList = new List<ParticipantResource>();
            var partEnum = participants.GetEnumerator();
            while (partEnum.MoveNext())
                participantList.Add(partEnum.Current);

            foreach (var participant in participantList)
            {
                if (conferenceRecord.PhoneCall2SID == participant.CallSid)
                {
                    retVal = true;
                    break;
                }
            }
            return retVal;
        }

        [System.Web.Mvc.HttpPost]
        public TwiMLResult HandleCallStatusCallback(TCallRequest request)
        {
            //Uncomment below to print full
            //request body
            //var req = Request.InputStream;
            //var json = new StreamReader(req).ReadToEnd();
            //conferenceServices.LogMessage(json);

            var response = new VoiceResponse();

            var requestCallStatus = request.CallStatus;
            var requestCallSId = request.CallSid;

            var requestFrom = request.From;

            var id = Request.QueryString["id"];
            var conferenceSid = Request.QueryString["conferenceSid"];
            var conferenceName = Request.QueryString["conferenceName"];
            var TwilioPhoneNumber = Request.QueryString["TwilioPhoneNumber"];

            int conferenceRecordId = Int32.Parse(id);

            switch (requestCallStatus)
            {
                //case "initiated":
                //    conferenceServices.LogMessage(string.Format("Step 4 Connecting Participant requestCallSId {0}  conferenceSid {1}  requestCallStatus {2} ", requestCallStatus, requestCallSId, conferenceSid), 4, conferenceRecordId);
                //    break;

                //case "ringing":
                //    conferenceServices.LogMessage(string.Format("Step 5 Connecting Participant requestCallSId {0}  conferenceSid {1}  requestCallStatus {2} ", requestCallStatus, requestCallSId, conferenceSid), 5, conferenceRecordId);
                //    break;

                //case "in-progress":
                //    conferenceServices.LogMessage(string.Format("Step 6 Connecting Participant requestCallStatus {0}  conferenceSid {1}  requestCallSId {2} ", requestCallStatus, requestCallSId, conferenceSid), 6, conferenceRecordId);
                //    break;

                // Add message to user here by connecting to the confernce before closing the conference
                case "failed":
                case "no-answer":
                case "busy":
                    ConnectTwilioBot(errorCode: requestCallStatus,
                                    conferenceRecordId: conferenceRecordId,
                                    SERVICE_USER_TWILIO_PHONE_NUMBER: TwilioPhoneNumber);
                    conferenceServices.LogMessage(string.Format("Step 6 Connecting Participant requestCallStatus {0}  conferenceSid {1}  requestCallSId {2} ", requestCallStatus, requestCallSId, conferenceSid), 6, conferenceRecordId);
                    break;

                default:
                    break;
            }

            return new TwiMLResult(response);
        }

        //[System.Web.Mvc.HttpPost]
        //public TwiMLResult HandleCallStatusFallback(VoiceRequest request)
        //{
        //    //Uncomment below to print full
        //    //request body
        //    //var req = Request.InputStream;
        //    //var json = new StreamReader(req).ReadToEnd();
        //    //conferenceServices.LogMessage(json);

        //    var response = new VoiceResponse();
        //    conferenceServices.LogMessage("In handle conference event");
        //    //var requestCallStatus = request.CallStatus;
        //    //var requestCallSId = request.CallSid;

        //    //var requestFrom = request.From;

        //    //var id = Request.QueryString["id"];
        //    //var conferenceSid = Request.QueryString["conferenceSid"];
        //    //var conferenceName = Request.QueryString["conferenceName"];
        //    //var TwilioPhoneNumber = Request.QueryString["TwilioPhoneNumber"];

        //    //int conferenceRecordId = Int32.Parse(id);
        //    conferenceServices.LogMessage("In HandleCallStatusFallback");
        //    //conferenceServices.LogMessage("Request status " + request.DialCallStatus);
        //    return new TwiMLResult(response);
        //}


        //[System.Web.Mvc.HttpPost]
        //public TwiMLResult HandleConferenceEvent(VoiceRequest request)
        //{
        //    //Uncomment below to print full
        //    //request body
        //    //var req = Request.InputStream;
        //    //var json = new StreamReader(req).ReadToEnd();
        //    //conferenceServices.LogMessage(json);

        //    var response = new VoiceResponse();
            
        //    var requestCallStatus = request.CallStatus;
        //    var requestCallSId = request.CallSid;

        //    var requestFrom = request.From;

        //    var id = Request.QueryString["id"];
        //    var conferenceSid = Request.QueryString["conferenceSid"];
        //    var conferenceName = Request.QueryString["conferenceName"];
        //    var TwilioPhoneNumber = Request.QueryString["TwilioPhoneNumber"];

        //    int conferenceRecordId = Int32.Parse(id);
        //    conferenceServices.LogMessage("In handle conference event");
        //    conferenceServices.LogMessage("Request status " + request.DialCallStatus);
        //    return new TwiMLResult(response);
        //}


        public void  ConnectTwilioBot(string errorCode, int conferenceRecordId,string SERVICE_USER_TWILIO_PHONE_NUMBER)
        {
            var response = new VoiceResponse();
            try
            {
                var connectUrl = "";
                switch (errorCode)
                {
                    case "failed":
                        connectUrl = string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/ConnectTwilioBotFailedStatus?conferenceID={0}",conferenceRecordId);
                        break;
                    case "no-answer":
                        connectUrl = string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/ConnectTwilioBotNoAnswerStatus?conferenceID={0}", conferenceRecordId);
                        break;
                    case "busy":
                        connectUrl = string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/ConnectTwilioBotBusyStatus?conferenceID={0}", conferenceRecordId);
                        break;
                    default:
                        break;
                }

                // Authorize 
                //TwilioClient.Init(TWILIO_ACCOUNT_SID, TWILIO_ACCOUNT_TOKEN);

                #region Experimental Code
                //                string postUrl =
                //                string.Format("https://api.twilio.com/2010-04-01/Accounts/{0}/Calls.json", TWILIO_ACCOUNT_SID);
                //                WebRequest myReq = WebRequest.Create(postUrl);
                //                string credentials = string.Format("{0}:{1}", TWILIO_ACCOUNT_SID, TWILIO_ACCOUNT_TOKEN);

                //                myReq.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials));
                //                string formencodeddata = string.Format("To=+1{0}&From=+1{1}&Url={2}"
                //                    , "4159186602"
                //                    , TWILIO_BOT_NUMBER
                //                    , connectUrl);
                //                byte[] formbytes = System.Text.ASCIIEncoding.Default.GetBytes(formencodeddata);
                //                myReq.Method = "POST";
                //                myReq.ContentType = "application/x-www-form-urlencoded";
                //                conferenceServices.LogMessage("credentials " + credentials.ToString(), conferenceRecordId);
                //                conferenceServices.LogMessage("get request stream " + myReq.GetRequestStream(), conferenceRecordId);
                //                conferenceServices.LogMessage("formencodeddata " + formencodeddata.ToString(), conferenceRecordId);
                //                using (Stream postStream = myReq.GetRequestStream())
                //                {
                //                    postStream.Write(formbytes, 0, formbytes.Length);
                //                }

                ////                conferenceServices.LogMessage(string.Format("Step 8 Message Job  End: {0}", conferenceSid), 8, id);
                //                WebResponse webResponse = myReq.GetResponse();
                //                Stream receiveStream = webResponse.GetResponseStream();
                //                StreamReader reader = new StreamReader(receiveStream, Encoding.UTF8);
                //                string content = reader.ReadToEnd();
                //                conferenceServices.LogMessage(content);
                //                reader.Close();
                //                reader.Dispose();
                //                receiveStream.Close();
                //                receiveStream.Dispose();
                //                webResponse.Close();
                //                webResponse.Dispose();

                #endregion

                var call = CallResource.Create(
                to: new PhoneNumber(SERVICE_USER_TWILIO_PHONE_NUMBER)
                , from: new PhoneNumber("1" + TWILIO_BOT_NUMBER)
                , url: new Uri(connectUrl)                
                , method: Twilio.Http.HttpMethod.Post);

                #region Experimental Code
                ////conferenceServices.LogMessage("In ConnectTwilioBot Call SID  " + call.Sid, 0, conferenceRecordId);
                ////ConferenceResource.Update()
                ////var dial = new Dial();
                ////dial.Conference(conferenceName);
                ////response.Append(dial);

                //conferenceServices.LogMessage("Conference is connected " + UserConnected.ToString());
                ////ConferenceResource.Update(conferenceSid, TWILIO_ACCOUNT_SID, ConferenceResource.UpdateStatusEnum.Completed); 
                #endregion
            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
                throw;
            }
            //return new TwiMLResult(response);
            return;
        }


        public TwiMLResult ConnectTwilioBotFailedStatus(VoiceRequest request)
        {
            int conferenceID  = int.Parse(Request.QueryString["conferenceID"]);      
            conferenceServices.LogMessage("Step 6 Playing Message Before Conference Close ",6, conferenceID);
            var response = new VoiceResponse();
            Response.ContentType = "text/xml";
            response.Pause(1);
            response.Say("The user could not be reached at this time. You can hang up or wait a short while and the system will disconnect you");
            response.Hangup();
            return new TwiMLResult(response);
        }

        public TwiMLResult ConnectTwilioBotBusyStatus(VoiceRequest request)
        {
            int conferenceID = int.Parse(Request.QueryString["conferenceID"]);
            conferenceServices.LogMessage("Step 6 Playing Message Before Conference Close ", 6, conferenceID);
            var response = new VoiceResponse();
            Response.ContentType = "text/xml";
            response.Pause(1);
            response.Say("The user is busy in another call. You can hang up or wait a short while and the system will disconnect you");
            response.Hangup();
            return new TwiMLResult(response);
        }

        public TwiMLResult ConnectTwilioBotNoAnswerStatus(VoiceRequest request)
        {
            int conferenceID = int.Parse(Request.QueryString["conferenceID"]);

            conferenceServices.LogMessage("Step 6 Playing Message Before Conference Close ", 6, conferenceID);
            var response = new VoiceResponse();
            Response.ContentType = "text/xml";
            response.Pause(1);
            response.Say("The user could not be reached at this time. You can hang up or wait a short while and the system will disconnect you");
            response.Hangup();
            return new TwiMLResult(response);
        }

        public ActionResult ConnectTwilioBotMessage(VoiceRequest request)
        {
            int conferenceRecordId = int.Parse(Request.QueryString["id"]);
            conferenceServices.LogMessage("Step 8 Playing Message ", 8,conferenceRecordId);
            var response = new VoiceResponse();
            Response.ContentType = "text/xml";
            response.Pause(1);
            response.Say("This conference call will be ending in 1 minute");
            response.Hangup();
            return new TwiMLResult(response);
        }

        public ActionResult ConnectTwilioBotWarning(VoiceRequest request)
        {
            int conferenceRecordId = int.Parse(Request.QueryString["id"]);
            conferenceServices.LogMessage("Step 9 Playing Warning ",9, conferenceRecordId);
            var response = new VoiceResponse();
            Response.ContentType = "text/xml";
            response.Pause(1);
            response.Say("this call will be ending shortly");
            return new TwiMLResult(response);
        }

        enum MyEnum 
        {
            start,end,join
        }


        public TwiMLResult ConferenceInPerson2(VoiceRequest request)
        {
            var response = new VoiceResponse();
            try
            {
                string conferenceName = Request.QueryString["conferenceName"];
                int id = Int32.Parse(Request.QueryString["id"]);

                TwilioConferenceCall conferenceRecord = conferenceServices.GetConferenceRecord(id);

                response.Pause(1);
                //response.Say("You are about to join a conference call");
                //response.Say("We are going to conference you in with :");


                response.Say("We are about to Conference you with ");

                foreach (var digit in conferenceRecord.PhoneFrom)
                {
                    response.Say(digit.ToString());
                }

                var dial = new Dial();
                dial.Timeout = TIMEOUT_INTERVAL;
                dial.Conference(conferenceName);
                response.Append(dial);
                

            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
                throw;
            }
            return new TwiMLResult(response);
        }


        #endregion

        #region Methods

        private void CalculateCallStartTimeValues(out ZonedDateTime utcCallStartTime, out DateTimeZone tzTargetDateTime, out ZonedDateTime targetCallStartTime)
        {
            try
            {
                LocalDateTime callStartTime = new
                    LocalDateTime(crCurrentCall.StartTime.Value.Year,
                                        crCurrentCall.StartTime.Value.Month,
                                            crCurrentCall.StartTime.Value.Day,
                                                    crCurrentCall.StartTime.Value.Hour,
                                                        crCurrentCall.StartTime.Value.Minute,
                                                                crCurrentCall.StartTime.Value.Second);
                
                utcCallStartTime = callStartTime.InZoneLeniently(dtzsourceDateTime);
                tzTargetDateTime = DateTimeZoneProviders.Tzdb[strTargetTimeZoneID];
                targetCallStartTime = utcCallStartTime.WithZone(tzTargetDateTime);

            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
                throw;
            }
        }

        private void CalculateConferenceStartTimeValues(out ZonedDateTime utcConferenceStartTime, out DateTimeZone tzTargetDateTime, out ZonedDateTime targetCallStartTime)
        {

            try
            {
                LocalDateTime conferenceStartTime = new
                    LocalDateTime(crCurrentConference.DateCreated.Value.Year,
                                        crCurrentConference.DateCreated.Value.Month,
                                            crCurrentConference.DateCreated.Value.Day,
                                                    crCurrentConference.DateCreated.Value.Hour,
                                                        crCurrentConference.DateCreated.Value.Minute,
                                                                crCurrentConference.DateCreated.Value.Second);
                
                utcConferenceStartTime = conferenceStartTime.InZoneLeniently(dtzsourceDateTime);
                tzTargetDateTime = DateTimeZoneProviders.Tzdb[strTargetTimeZoneID];
                targetCallStartTime = utcConferenceStartTime.WithZone(tzTargetDateTime);

            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
                throw;
            }
            
        }

        public static DateTime ToDateTime(ZonedDateTime zonedDateTime)
        {
            return new DateTime(zonedDateTime.Year, zonedDateTime.Month, zonedDateTime.Day, zonedDateTime.Hour, zonedDateTime.Minute, zonedDateTime.Second);
        }

        public TwiMLResult ReturnHoldMusicURI()
        {
            var response = new VoiceResponse();           
            response.Play(new Uri("http://com.twilio.music.ambient.s3.amazonaws.com/aerosolspray_-_Living_Taciturn.mp3"));
            return TwiML(response);
        }

        private string ConnectParticipant(string phoneTo, string TwilioPhoneNumber, string conferenceName, string ConferenceSid, int conferenceRecordId)
        {
            var callSID = "";
            var statusCallbackEventlist = new List<String>() {
            "initiated",
            "ringing",
            "answered",
            "completed",
            };

            //conferenceServices.LogMessage("ConnectParticipant TwilioPhoneNumber " + TwilioPhoneNumber);

            try
            {
             var call = CallResource.Create(
                 from: new PhoneNumber(TwilioPhoneNumber),
                 to: new PhoneNumber(phoneTo),
                 statusCallbackEvent : statusCallbackEventlist,
                 statusCallback : new Uri(string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/HandleCallStatusCallback?id={0}&conferenceSid={1}&conferenceName={2}&TwilioPhoneNumber={3}", conferenceRecordId,ConferenceSid,conferenceName, TwilioPhoneNumber)),
                 statusCallbackMethod: Twilio.Http.HttpMethod.Post,
                 timeout:TIMEOUT_INTERVAL,                                      // Number of seconds that the system wil attempt to make call (after which the system will hang up                    
                 url: new Uri(string.Format("https://callingserviceproduction.azurewebsites.net//twilioconference/ConferenceInPerson2?conferenceName={0}&id={1}",conferenceName,conferenceRecordId)));

                 callSID = call.Sid;
            }
            catch (Exception ex)
            {
                conferenceServices.ErrorMessage(string.Format(
                                   "|Error Message - {0}" +
                                   "| 1.Source {1} " +
                                   "| 2.Trace {2} " +
                                   "| 3.Inner Exception {3} |",
                                   ex.Message,
                                   ex.Source,
                                   ex.StackTrace,
                                   ex.InnerException));
            }
            return callSID;
        }


        private bool CallStartAtTimeSlot
                              (int zdtCallStartMinute,
                                   int zdtCallStartSecond,
                                        ref int intMinutesToPause,
                                            ref int intSecondsToPause,
                                              ref string strHourMessage,
                                                ref double messageIntervalinSeconds,
                                                    ref double hangupIntervalinSeconds,
                                                      ref double warningIntervalinSeconds)
        {

            ///*******************************
            /// Assume Call start time is 19:47:34
            // zdtCall Start start minute is 47
            // zdtCall Start start second is 34
            ///*******************************
           
            var retVal = false;
            var blnCallStartTimeAtTimeSlot = false;

            try
            {
                blnCallStartTimeAtTimeSlot =
                   (((zdtCallStartMinute % 10) == 0)          // The start minute is either exactly at  00 / 10 / 20 / 30 / 40 / 50 past the hour
                      && (zdtCallStartSecond <= 59));

                // PRODUCTION CHANGE (uncomment lines below)
                // Keep both message & hangup interval at default values of 8 & 9 minutes for production app
                messageIntervalinSeconds = (8 * 60);  //480
                warningIntervalinSeconds = (510);
                hangupIntervalinSeconds = (9 * 60);  // 540

                // PRODUCTION CHANGE (comment lines below)
                // Keep both message & hangup interval at default values of 8 & 9 minutes for test app
                //messageIntervalinSeconds = 30;
                //warningIntervalinSeconds = 60;
                //hangupIntervalinSeconds = 90;

                // Find total absolute seconds of call start time     
                // If call start minute is 47 and call start seconds is 34
                // intCallStartTotalSeconds = 2854
                var intCallStartTotalSeconds =
                    zdtCallStartSecond             //Second the call started
                      + (zdtCallStartMinute * 60); //Minute the call started

                // Fid time slot end
                // IF call start minute is 47 then div = 4
                var div = zdtCallStartMinute / 10;
                // time slot end is 50 
                var timeslotend = (10 * div) + 10;
                switch (timeslotend)
                {
                    case 60:
                        strHourMessage = "at the Top of the hour";
                        break;
                    case 10:
                        strHourMessage = "at ten minutes past the hour";
                        break;
                    case 20:
                        strHourMessage = "at twenty minutes past the hour";
                        break;
                    case 30:
                        strHourMessage = "at thirty minutes past the hour";
                        break;
                    case 40:
                        strHourMessage = "at fourty minutes past the hour";
                        break;
                    case 50:
                        strHourMessage = "at fifty minutes past the hour";
                        break;
                }

                // Find total absolute seconds of call slot end time
                // In current case this value is 3000 (60 * 50)
                var intCallSlotEndTotalSeconds = timeslotend * 60;

                // Convert to minutes & seconds
                // intMinutesToPause = (3000 - 2854) / 60 = 2 minutes
                intMinutesToPause = (intCallSlotEndTotalSeconds - intCallStartTotalSeconds) / 60;
                // intSecondsToPause = (3000 - 2854) % 60 = 26 seconds
                intSecondsToPause = (intCallSlotEndTotalSeconds - intCallStartTotalSeconds) % 60;

                if ((blnCallStartTimeAtTimeSlot) && (intSecondsToPause > 0)) // This effecively means that the call has stated within a minute of the call slot
                {
                    // PRODUCTION CHANGE (Uncomment lines below)
                    // Total 8 minutes into message, convert to seconds and adjust for 26 seconds into call
                    messageIntervalinSeconds = (8 * 60) - (60 - intSecondsToPause);
                    ///// Total 9 minutes into message, convert to seconds and adjust for 26 seconds into call
                    hangupIntervalinSeconds = (9 * 60) - (60 - intSecondsToPause);

                    // PRODUCTION CHANGE (comment lines below)
                    // Total 8 minutes into message, convert to seconds and adjust for 26 seconds into call
                    //messageIntervalinSeconds = (1 * 60) - (60 - intSecondsToPause);
                    // Total 9 minutes into message, convert to seconds and adjust for 26 seconds into call
                    //hangupIntervalinSeconds = (2 * 60) - (60 - intSecondsToPause);


                    warningIntervalinSeconds = hangupIntervalinSeconds - 10;

                }

            }
            catch (Exception)
            {

                throw;
            }
            retVal = blnCallStartTimeAtTimeSlot;
            return retVal;
        }

        #endregion

        #region Properties
        internal ConferenceResource crCurrentConference
        {
            get
            {
                _crCurrentConference = _crCurrentConference ??
                     ConferenceResource.Fetch(strConferenceSID);
                return _crCurrentConference;
            }
        }

        internal DateTimeZone dtzsourceDateTime
        {
            get { return DateTimeZoneProviders.Tzdb[strUTCTimeZoneID]; }
        }

        internal CallResource crCurrentCall
        {
            get
            {
                _crCurrentCall = _crCurrentCall ??
                     CallResource.Fetch(strCallSID);
                return _crCurrentCall;
            }
        }

        internal string strCallSID
        {
            get
            {
                _strCallSID = _strCallSID ??
                        (!string.IsNullOrEmpty(Request.Params["CallSid"])
                        ? Request.Params["CallSid"].ToString()
                        : string.Empty);

                return _strCallSID;
            }
        }

        internal string strConferenceSID
        {
            get
            {
                _strConferenceSID = _strConferenceSID ??
                        (!string.IsNullOrEmpty(Request.Params["ConferenceSid"])
                        ? Request.Params["ConferenceSid"].ToString()
                        : string.Empty);

                return _strConferenceSID;
            }
        }


        #endregion

    }
}


