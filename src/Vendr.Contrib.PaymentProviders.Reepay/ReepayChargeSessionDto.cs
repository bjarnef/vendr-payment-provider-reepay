﻿using System.Runtime.Serialization;

namespace Vendr.Contrib.PaymentProviders.Reepay
{
    [DataContract]
    public class ReepaySessionChargeResultDto
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "url")]
        public string Url { get; set; }
    }
}
