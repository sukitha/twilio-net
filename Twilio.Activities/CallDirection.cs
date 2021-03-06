﻿namespace Twilio.Activities
{

    /// <summary>
    /// Direction of a call.
    /// </summary>
    public enum CallDirection
    {

        /// <summary>
        /// Direction of the call is unknown.
        /// </summary>
        Unknown,

        /// <summary>
        /// Call originated into Twilio.
        /// </summary>
        Inbound,
        
        /// <summary>
        /// Call originated from Twilio.
        /// </summary>
        Outbound,

        /// <summary>
        /// Call originated from an API request.
        /// </summary>
        OutboundApi,

        OutboundDial,

    }

}
