﻿using System;
using System.Activities;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.DurableInstancing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Web;
using System.Xml;
using System.Xml.Linq;

namespace Twilio.Activities
{

    /// <summary>
    /// Extend and expose this HTTP handler to direct the Twilio application towards. Implement InitializeActivity to
    /// generate and return the <see cref="Activity"/> that is executed by the workflow engine. Session state is
    /// currently required to store the ongoing workflows.
    /// </summary>
    public abstract class HttpHandler : IHttpHandler, ITwilioContext
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public HttpHandler()
        {
            TwilioResponse = CurrentTwilioElement = new XElement("Response");
        }

        public bool IsReusable
        {
            get { return false; }
        }

        /// <summary>
        /// Gets the <see cref="HttpContext"/> for the current Twilio request.
        /// </summary>
        public HttpContext Context { get; private set; }

        /// <summary>
        /// Gets the <see cref="HttpRequest"/>.
        /// </summary>
        public HttpRequest Request
        {
            get { return Context.Request; }
        }

        /// <summary>
        /// Gets the <see cref="HttpResponse"/> for the current Twilio request.
        /// </summary>
        public HttpResponse Response
        {
            get { return Context.Response; }
        }

        /// <summary>
        /// Gets the <see cref="Activity"/> to be used to serve Twilio calls.
        /// </summary>
        public Activity Activity { get; private set; }

        /// <summary>
        /// Context into which events occuring during the execution of the workflow go.
        /// </summary>
        RunnableSynchronizationContext SynchronizationContext { get; set; }

        /// <summary>
        /// Gets the root <see cref="XElement"/> of the generated TwiML.
        /// </summary>
        public XElement TwilioResponse { get; private set; }

        /// <summary>
        /// Gets the current <see cref="XElement"/> in which to add new TwiML nodes.
        /// </summary>
        public XElement CurrentTwilioElement { get; private set; }

        /// <summary>
        /// Unhandled exception caused by execution.
        /// </summary>
        public ExceptionDispatchInfo UnhandledExceptionInfo { get; private set; }

        /// <summary>
        /// Gets the raw request <see cref="Uri"/>.
        /// </summary>
        public Uri RequestUri
        {
            get { return new Uri(Request.Url.GetLeftPart(UriPartial.Path)); }
        }

        /// <summary>
        /// Gets the <see cref="Uri"/> relative to the current request. That is, the current request's parent directory.
        /// </summary>
        public Uri RelativeUri
        {
            get { return new Uri(RequestUri.AbsoluteUri.Remove(RequestUri.AbsoluteUri.Length - RequestUri.Segments.Last().Length)); }
        }

        /// <summary>
        /// Gets the <see cref="Uri"/> to post back to and resume the workflow.
        /// </summary>
        public Uri SelfUrl
        {
            // include session and instance
            get { return AppendQueryArgToUri(SessionIDManager.ApplySessionIDQueryArg(Request.Url), "InstanceId", WfApplication.Id.ToString()); }
        }

        /// <summary>
        /// Gets a reference to the loaded Workflow Application.
        /// </summary>
        WorkflowApplication WfApplication { get; set; }

        /// <summary>
        /// Override this method to initialize and return the activity to be executed.
        /// </summary>
        protected abstract Activity CreateActivity();

        /// <summary>
        /// Override this method to create the instance store used to serialize workflow instances.
        /// </summary>
        /// <returns></returns>
        protected virtual InstanceStore CreateInstanceStore()
        {
            return new HttpCookieInstanceStore(Context);
        }

        /// <summary>
        /// Appends the given name and value to the query string.
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Uri AppendQueryArgToUri(Uri uri, string name, string value)
        {
            if (uri == null)
                throw new ArgumentNullException("uri");
            if (name == null)
                throw new ArgumentNullException("name");

            // parse query string and update session id
            var q = HttpUtility.ParseQueryString(uri.Query);
            q[name] = value;

            // rebuild uri with new query string
            return new Uri(uri, "?" + string.Join("&", q.AllKeys
                .Select(i => HttpUtility.UrlEncode(i) + "=" + HttpUtility.UrlEncode(q[i]))));
        }

        /// <summary>
        /// Gets the <see cref="Uri"/> to post back and resume the workflow with the given bookmark.
        /// </summary>
        /// <param name="bookmarkName"></param>
        /// <returns></returns>
        public Uri BookmarkSelfUrl(string bookmarkName)
        {
            return RelativeUri.MakeRelativeUri(AppendQueryArgToUri(SelfUrl, "Bookmark", bookmarkName));
        }

        /// <summary>
        /// Invoked when a web request arrives.
        /// </summary>
        public void ProcessRequest(HttpContext context)
        {
            // bind ourselves to the context
            Context = context;

            // obtain our activity instance
            Activity = CreateActivity();

            // initializes workflow application and appropriate call backs
            WfApplication = new WorkflowApplication(Activity);
            WfApplication.Extensions.Add<ITwilioContext>(() => this);
            WfApplication.SynchronizationContext = SynchronizationContext = new RunnableSynchronizationContext();
            WfApplication.InstanceStore = CreateInstanceStore();
            WfApplication.Aborted = OnAborted;
            WfApplication.Completed = OnCompleted;
            WfApplication.Idle = OnIdle;
            WfApplication.OnUnhandledException = OnUnhandledException;
            WfApplication.PersistableIdle = OnPersistableIdle;
            WfApplication.Unloaded = OnUnloaded;

            // attempt to resolve current instance id and reload workflow state
            if (Request["InstanceId"] != null)
                WfApplication.Load(Guid.Parse(Request["InstanceId"]));

            // postback to resume a bookmark
            if (Request["Bookmark"] != null)
            {
                var result = GetBookmarkData();
                if (result != null)
                    WfApplication.BeginResumeBookmark(Request["Bookmark"], result, i => WfApplication.EndResumeBookmark(i), null);
            }

            // begin running the application
            WfApplication.BeginRun(i => WfApplication.EndRun(i), null);

            // process any outstanding events until completion
            SynchronizationContext.Run();

            // throw exception
            if (UnhandledExceptionInfo != null)
                UnhandledExceptionInfo.Throw();

            // write finished twilio output
            context.Response.ContentType = "text/xml";
            using (var wrt = XmlWriter.Create(Response.Output))
                TwilioResponse.WriteTo(wrt);
        }

        void OnAborted(WorkflowApplicationAbortedEventArgs args)
        {
            if (args.Reason != null)
                UnhandledExceptionInfo = ExceptionDispatchInfo.Capture(args.Reason);
        }

        void OnCompleted(WorkflowApplicationCompletedEventArgs args)
        {
            // we're done, hang up on the user
            TwilioResponse.Add(new XElement("Hangup"));
        }

        void OnIdle(WorkflowApplicationIdleEventArgs args)
        {
            // if the response is empty, redirect back to ourselves periodically
            if (!TwilioResponse.HasElements)
            {
                TwilioResponse.Add(new XElement("Pause", 2));
                TwilioResponse.Add(new XElement("Redirect", SelfUrl));
            }
        }

        UnhandledExceptionAction OnUnhandledException(WorkflowApplicationUnhandledExceptionEventArgs args)
        {
            // save unhandled exception to be thrown
            if (args.UnhandledException != null)
                UnhandledExceptionInfo = ExceptionDispatchInfo.Capture(args.UnhandledException);

            // abort workflow
            return UnhandledExceptionAction.Abort;
        }

        PersistableIdleAction OnPersistableIdle(WorkflowApplicationIdleEventArgs args)
        {
            return PersistableIdleAction.Unload;
        }

        void OnUnloaded(WorkflowApplicationEventArgs args)
        {
            // end event loop
            SynchronizationContext.Complete();
        }

        /// <summary>
        /// Extracts the data to be passed to a bookmark.
        /// </summary>
        /// <returns></returns>
        NameValueCollection GetBookmarkData()
        {
            // extract all query arguments if bookmark specified
            if (Request["Bookmark"] != null)
            {
                // combine query string and form variables
                var q = new NameValueCollection(Request.QueryString);
                foreach (var i in Request.Form.AllKeys)
                    q[i] = Request.Form[i];
                return q;
            }

            return null;
        }

        /// <summary>
        /// Parses a call status string into the proper type.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        CallStatus ParseCallStatus(string s)
        {
            switch (s)
            {
                case "completed":
                    return CallStatus.Completed;
                case "busy":
                    return CallStatus.Busy;
                case "no-answer":
                    return CallStatus.NoAnswer;
                case "failed":
                    return CallStatus.Failed;
                case "canceled":
                    return CallStatus.Canceled;
                default:
                    throw new FormatException();
            }
        }

        Uri ITwilioContext.SelfUrl
        {
            get { return SelfUrl; }
        }

        Uri ITwilioContext.BookmarkSelfUri(string bookmarkName)
        {
            return BookmarkSelfUrl(bookmarkName);
        }

        XElement ITwilioContext.Response
        {
            get { return TwilioResponse; }
        }

        XElement ITwilioContext.Element
        {
            get { return CurrentTwilioElement; }
            set { CurrentTwilioElement = value; }
        }

    }

}