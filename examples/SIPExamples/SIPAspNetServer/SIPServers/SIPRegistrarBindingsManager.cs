﻿// ============================================================================
// FileName: SIPRegistrarBindingsManager.cs
//
// Description:
// Manages the storing, updating and retrieval of bindings for a SIP Registrar.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 21 May 2009	Aaron Clauson	Created.
// 29 Dec 2020  Aaron Clauson   Added to server project.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Transactions;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using demo.DataAccess;

namespace demo
{
    public class SIPRegistrarBindingsManager
    {
        private const string EXPIRE_BINDINGS_THREAD_NAME = "sipregistrar-expirebindings";
        private const int CHECK_REGEXPIRY_DURATION = 1000;            // Period at which to check for expired bindings.
        public const int NATKEEPALIVE_DEFAULTSEND_INTERVAL = 10;
        private const int MAX_USERAGENT_LENGTH = 128;
        public const int MINIMUM_EXPIRY_SECONDS = 120;
        private const int DEFAULT_BINDINGS_PER_USER = 3;              // The default maixmim number of bindings that will be allowed for each unique SIP account.
        private const int REMOVE_EXPIRED_BINDINGS_INTERVAL = 3000;    // The interval in seconds at which to check for and remove expired bindings.
        private const int SEND_NATKEEPALIVES_INTERVAL = 5000;
        private const int BINDING_EXPIRY_GRACE_PERIOD = 10;
        private const int DEFAULT_MAX_EXPIRY_SECONDS = 7200;

        private string m_sipRegisterRemoveAll = SIPConstants.SIP_REGISTER_REMOVEALL;
        private string m_sipExpiresParameterKey = SIPContactHeader.EXPIRES_PARAMETER_KEY;

        private readonly ILogger Logger = SIPSorcery.LogFactory.CreateLogger<SIPRegistrarBindingsManager>();

        private SIPRegistrarBindingDataLayer m_registrarBindingDataLayer;
        private int m_maxBindingsPerAccount;
        private bool m_stop;

        public SIPRegistrarBindingsManager(
            SIPRegistrarBindingDataLayer registrarBindingDataLayer,
            int maxBindingsPerAccount = DEFAULT_BINDINGS_PER_USER)
        {
            m_maxBindingsPerAccount = maxBindingsPerAccount;
            m_registrarBindingDataLayer = registrarBindingDataLayer;
        }

        public void Start()
        {
            ThreadPool.QueueUserWorkItem(delegate { ExpireBindings(); });
        }

        public void Stop()
        {
            m_stop = true;
        }

        private void ExpireBindings()
        {
            Thread.CurrentThread.Name = EXPIRE_BINDINGS_THREAD_NAME;

            while (!m_stop)
            {
                try
                {
                    DateTime expiryTime = DateTime.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1);
                    SIPRegistrarBinding expiredBinding = GetNextExpiredBinding(expiryTime);

                    while (expiredBinding != null)
                    {
                        Logger.LogDebug("Expired binding deleted for " + expiredBinding.SIPAccount.AOR + " and " + expiredBinding.ContactURI + ", last register " +
                            expiredBinding.LastUpdate.ToString("o") + ", expiry " + expiredBinding.Expiry + "s, expiry time " + expiredBinding.ExpiryTime.ToString("o") + ", now " + expiryTime.ToString("o") + ".");

                        expiryTime = DateTime.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1);
                        expiredBinding = GetNextExpiredBinding(expiryTime);
                    }
                }
                catch (Exception expireExcp)
                {
                    Logger.LogError("Exception ExpireBindings Delete. " + expireExcp.Message);
                }

                Thread.Sleep(REMOVE_EXPIRED_BINDINGS_INTERVAL);
            }

            Logger.LogDebug($"Thread {EXPIRE_BINDINGS_THREAD_NAME} stopped!");
        }

        private SIPRegistrarBinding GetNextExpiredBinding(DateTime expiryTime)
        {
            //using (var trans = new TransactionScope())
            //{
                SIPRegistrarBinding binding = m_registrarBindingDataLayer.GetNextExpired(expiryTime);

                if (binding != null)
                {
                    if (binding.ExpiryTime < DateTime.UtcNow.AddSeconds(BINDING_EXPIRY_GRACE_PERIOD * -1))
                    {
                        m_registrarBindingDataLayer.Delete(binding.ID);
                    }
                    else
                    {
                        Logger.LogWarning("A binding returned from the database as expired wasn't. " + binding.ID + " and " + binding.MangledContactURI + ", last register " +
                                binding.LastUpdate.ToString("HH:mm:ss") + ", expiry " + binding.Expiry + ", expiry time " + binding.ExpiryTime.ToString("HH:mm:ss") +
                                ", checkedtime " + expiryTime.ToString("HH:mm:ss") + ", now " + DateTime.UtcNow.ToString("HH:mm:ss") + ".");

                        binding = null;
                    }
                }

                //trans.Complete();

                return binding;
            //}
        }

        /// <summary>
        /// Updates the bindings list for a registrar's address-of-records.
        /// </summary>
        /// <param name="proxyEndPoint">If the request arrived at this registrar via a proxy then this will contain the end point of the proxy.</param>
        /// <param name="uacRecvdEndPoint">The public end point the UAC REGISTER request was deemded to have originated from.</param>
        /// <param name="registrarEndPoint">The registrar end point the registration request was received on.</param>
        /// <param name="maxAllowedExpiry">The maximum allowed expiry that can be granted to this binding request.</param>
        /// <returns>If the binding update was successful the expiry time for it is returned otherwise 0.</returns>
        public List<SIPRegistrarBinding> UpdateBindings(
            SIPAccount sipAccount,
            SIPEndPoint proxySIPEndPoint,
            SIPEndPoint remoteSIPEndPoint,
            SIPEndPoint registrarSIPEndPoint,
            List<SIPContactHeader> contactHeaders,
            string callId,
            int cseq,
            int expiresHeaderValue,
            string userAgent,
            out SIPResponseStatusCodesEnum responseStatus,
            out string responseMessage)
        {
            //logger.Debug("UpdateBinding " + bindingURI.ToString() + ".");

            int maxAllowedExpiry = DEFAULT_MAX_EXPIRY_SECONDS;
            responseMessage = null;
            string sipAccountAOR = sipAccount.AOR;
            responseStatus = SIPResponseStatusCodesEnum.Ok;

            try
            {
                userAgent = (userAgent != null && userAgent.Length > MAX_USERAGENT_LENGTH) ? userAgent.Substring(0, MAX_USERAGENT_LENGTH) : userAgent;

                List<SIPRegistrarBinding> bindings = m_registrarBindingDataLayer.GetForSIPAccount(sipAccount.ID);

                foreach (SIPContactHeader contactHeader in contactHeaders)
                {
                    SIPURI bindingURI = contactHeader.ContactURI.CopyOf();
                    int contactHeaderExpiresValue = contactHeader.Expires;
                    int bindingExpiry = 0;

                    if (bindingURI.Host == m_sipRegisterRemoveAll)
                    {
                        if (contactHeaders.Count > 1)
                        {
                            // If a register request specifies remove all it cannot contain any other binding requests.
                            Logger.LogDebug("Remove all bindings requested for " + sipAccountAOR + " but mutliple bindings specified, rejecting as a bad request.");
                            responseStatus = SIPResponseStatusCodesEnum.BadRequest;
                            break;
                        }

                        #region Process remove all bindings.

                        if (expiresHeaderValue == 0)
                        {
                            // Removing all bindings for user.
                            Logger.LogDebug("Remove all bindings requested for " + sipAccountAOR + ".");

                            // Mark all the current bindings as expired.
                            if (bindings != null && bindings.Count > 0)
                            {
                                for (int index = 0; index < bindings.Count; index++)
                                {
                                    //bindings[index].RemovalReason = SIPBindingRemovalReason.ClientExpiredAll;
                                    bindings[index].Expiry = 0;
                                    //m_bindingsPersistor.Update(bindings[index]);
                                }
                            }

                            //FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingRemoval, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));

                            responseStatus = SIPResponseStatusCodesEnum.Ok;
                        }
                        else
                        {
                            // Remove all header cannot be present with other headers and must have an Expiry equal to 0.
                            responseStatus = SIPResponseStatusCodesEnum.BadRequest;
                        }

                        #endregion
                    }
                    else
                    {
                        int requestedExpiry = (contactHeaderExpiresValue != -1) ? contactHeaderExpiresValue : expiresHeaderValue;
                        requestedExpiry = (requestedExpiry == -1) ? maxAllowedExpiry : requestedExpiry;   // This will happen if the Expires header and the Expiry on the Contact are both missing.
                        bindingExpiry = (requestedExpiry > maxAllowedExpiry) ? maxAllowedExpiry : requestedExpiry;
                        bindingExpiry = (bindingExpiry < MINIMUM_EXPIRY_SECONDS) ? MINIMUM_EXPIRY_SECONDS : bindingExpiry;

                        bindingURI.Parameters.Remove(m_sipExpiresParameterKey);

                        //SIPRegistrarBinding binding = GetBindingForContactURI(bindings, bindingURI.ToString());
                        SIPRegistrarBinding binding = bindings.Where(x => x.ContactURI == bindingURI.ToString()).FirstOrDefault();

                        if (binding != null)
                        {
                            if (requestedExpiry <= 0)
                            {
                                Logger.LogDebug($"Binding expired by client for {sipAccountAOR} from {remoteSIPEndPoint}.");
                                bindings.Remove(binding);
                                m_registrarBindingDataLayer.Delete(binding.ID);
                                bindingExpiry = 0;
                            }
                            else
                            {
                                Logger.LogDebug($"Binding update request for {sipAccountAOR} from {remoteSIPEndPoint}, expiry requested {requestedExpiry}s granted {bindingExpiry}s.");
                                //binding.RefreshBinding(bindingExpiry, remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, sipAccount.DontMangleEnabled);
                                //binding.RefreshBinding(bindingExpiry, remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, false);

                                //DateTime startTime = DateTime.Now;
                                //m_bindingsPersistor.Update(binding);
                                //m_registrarBindingDataLayer.Update(binding);
                                m_registrarBindingDataLayer.RefreshBinding(binding.ID, bindingExpiry, remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, false);
                                //TimeSpan duration = DateTime.Now.Subtract(startTime);
                                //FireSIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegistrarTiming, "Binding database update time for " + sipAccountAOR + " took " + duration.TotalMilliseconds + "ms.", null));
                                //FireSIPMonitorLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingUpdate, sipAccount.Owner, sipAccount.Id.ToString(), SIPURI.ParseSIPURIRelaxed(sipAccountAOR)));
                            }
                        }
                        else
                        {
                            if (requestedExpiry > 0)
                            {
                                Logger.LogDebug($"New binding request for {sipAccountAOR} from {remoteSIPEndPoint}, expiry requested {requestedExpiry}s granted {bindingExpiry}s.");

                                if (bindings.Count >= m_maxBindingsPerAccount)
                                {
                                    // Need to remove the oldest binding to stay within limit.
                                    SIPRegistrarBinding oldestBinding = bindings.OrderBy(x => x.LastUpdate).Last();
                                    Logger.LogDebug($"Binding limit exceeded for {sipAccountAOR} from {remoteSIPEndPoint} removing oldest binding to stay within limit of {m_maxBindingsPerAccount}.");
                                    m_registrarBindingDataLayer.Delete(oldestBinding.ID);
                                }

                                SIPRegistrarBinding newBinding = new SIPRegistrarBinding(sipAccount, bindingURI, callId, cseq, userAgent,
                                    remoteSIPEndPoint, proxySIPEndPoint, registrarSIPEndPoint, bindingExpiry);
                                m_registrarBindingDataLayer.Add(newBinding);
                            }
                            else
                            {
                                Logger.LogDebug($"New binding received for {sipAccountAOR} with expired contact, {bindingURI} no update.");
                                bindingExpiry = 0;
                            }
                        }

                        responseStatus = SIPResponseStatusCodesEnum.Ok;
                    }
                }

                return m_registrarBindingDataLayer.GetForSIPAccount(sipAccount.ID);
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception UpdateBinding. " + excp);
                responseStatus = SIPResponseStatusCodesEnum.InternalServerError;
                return null;
            }
        }
    }
}
