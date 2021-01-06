﻿using System;
using System.Collections.Generic;

#nullable disable

namespace demo.DataAccess
{
    public partial class CDR
    {
        public CDR()
        {
            SIPCalls = new HashSet<SIPCall>();
        }

        public Guid ID { get; set; }
        public DateTime Inserted { get; set; }
        public string Direction { get; set; }
        public DateTime Created { get; set; }
        public string DstUser { get; set; }
        public string DstHost { get; set; }
        public string DstUri { get; set; }
        public string FromUser { get; set; }
        public string FromName { get; set; }
        public string FromHeader { get; set; }
        public string CallID { get; set; }
        public string LocalSocket { get; set; }
        public string RemoteSocket { get; set; }
        public Guid? BridgeID { get; set; }
        public DateTime? InProgressAt { get; set; }
        public int? InProgressStatus { get; set; }
        public string InProgressReason { get; set; }
        public int? RingDuration { get; set; }
        public DateTime? AnsweredAt { get; set; }
        public int? AnsweredStatus { get; set; }
        public string AnsweredReason { get; set; }
        public int? Duration { get; set; }
        public DateTime? HungupAt { get; set; }
        public string HungupReason { get; set; }

        public virtual ICollection<SIPCall> SIPCalls { get; set; }
    }
}
