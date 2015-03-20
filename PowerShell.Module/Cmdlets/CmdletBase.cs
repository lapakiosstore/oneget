﻿// 
//  Copyright (c) Microsoft Corporation. All rights reserved. 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  

namespace Microsoft.PowerShell.OneGet.Cmdlets {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Management.Automation;
    using System.Security;
    using System.Threading;
    using Microsoft.OneGet;
    using Microsoft.OneGet.Api;
    using Microsoft.OneGet.Implementation;
    using Microsoft.OneGet.Utility.Async;
    using Microsoft.OneGet.Utility.Extensions;
    using Microsoft.OneGet.Utility.Plugin;
    using Microsoft.OneGet.Utility.PowerShell;
    using Resources;
    using Utility;
    using Constants = OneGet.Constants;

    public delegate string GetMessageString(string messageId, string defaultText);

    public abstract class CmdletBase : AsyncCmdlet, IHostApi {
        private static int _globalCallCount = 1;
        private static readonly object _lockObject = new object();
        
        private readonly int _callCount;
        private readonly Hashtable _dynamicOptions = new Hashtable();

        [Parameter]
        public SwitchParameter Force;

        [Parameter]
        public SwitchParameter ForceBootstrap;

        private GetMessageString _messageResolver;

        protected CmdletBase() {
            _callCount = _globalCallCount++;
        }

        protected bool WaitForActivity<T>(IEnumerable<IAsyncEnumerable<T>> enumerables) {
            var handles = enumerables.Select(each => each.Ready).ConcatSingleItem(_cancellationEvent.Token.WaitHandle).ToArray();
            if (handles.Length > 1) {
                WaitHandle.WaitAny(handles);
                return !IsCanceled;
            }
            // we are finished when there is only handle to wait on (the cancellation token)
            return false;
        }
     
        protected abstract IEnumerable<string> ParameterSets {get;}

        protected bool IsPackageBySearch {
            get {
                return ParameterSetName == Constants.ParameterSets.PackageBySearchSet;
            }
        }

        protected bool IsPackageByObject {
            get {
                return ParameterSetName == Constants.ParameterSets.PackageByInputObjectSet;
            }
        }

        protected bool IsSourceByObject {
            get {
                return ParameterSetName == Constants.ParameterSets.SourceByInputObjectSet;
            }
        }

        protected internal IPackageManagementService PackageManagementService {
            get {
                lock (_lockObject) {
                    if (!IsCanceled && !IsInitialized) {
                        try {
                            IsInitialized = PackageManager.Instance.Initialize(this);
                        } catch (Exception e) {
                            e.Dump();
                        }
                    }
                }
                return PackageManager.Instance;
            }
        }

        protected int TimeOut {
            get {
                if (IsInvocation) {
                    var t = GetDynamicParameterValue<int>("Timeout");
                    if (t > 0) {
                        return t;
                    }
                }

                return Constants.DefaultTimeout; // in a non-invokcation, always default to one hour
            }
        }

        protected int Responsiveness {
            get {
                if (IsInvocation) {
                    var t = GetDynamicParameterValue<int>("Responsiveness");
                    if (t > 0) {
                        return t;
                    }
                }
                return Constants.DefaultResponsiveness; // in a non-invokcation, always default to one hour
            }
        }

        public GetMessageString MessageResolver {
            get {
                return _messageResolver ?? (_messageResolver = GetDynamicParameterValue<GetMessageString>("MessageResolver"));
            }
        }

        #region Implementing IHostApi

        public Hashtable DynamicOptions {
            get {
                return _dynamicOptions;
            }
        }

        public virtual IEnumerable<string> Sources {
            get {
                return null;
            }
            set {
            }
        }

        public virtual IEnumerable<string> OptionKeys {
            get {
                return DynamicParameterDictionary.Values.OfType<CustomRuntimeDefinedParameter>().Where(each => each.IsSet).Select(each => each.Name).Concat(MyInvocation.BoundParameters.Keys);
            }
        }

        protected bool GenerateCommonDynamicParameters() {
            if (IsInvocation) {
                // only generate these parameters if there is an actual call happening.
                // this prevents get-help from showing them.
                DynamicParameterDictionary.Add("Timeout", new RuntimeDefinedParameter("Timeout", typeof (int), new Collection<Attribute> {
                    new ParameterAttribute()
                }));

                DynamicParameterDictionary.Add("Responsiveness", new RuntimeDefinedParameter("Responsiveness", typeof (int), new Collection<Attribute> {
                    new ParameterAttribute()
                }));

                DynamicParameterDictionary.Add("MessageResolver", new RuntimeDefinedParameter("MessageResolver", typeof (GetMessageString), new Collection<Attribute> {
                    new ParameterAttribute()
                }));
            }
            return true;
        }

        public override string GetMessageString(string messageText, string defaultText) {
            messageText = DropMsgPrefix(messageText);

            if (string.IsNullOrWhiteSpace(defaultText) || defaultText.StartsWith("MSG:", StringComparison.OrdinalIgnoreCase)) {
                defaultText = Messages.ResourceManager.GetString(messageText);
            }

            string result = null;
            if (MessageResolver != null && MyInvocation.PipelineLength == 1 ) {
                // if the consumer has specified a MessageResolver delegate, we need to call it on the main thread
                // because powershell won't let us use the default runspace from another thread.

                // if we are anywhere but the end of the pipeline, the delegate here would block on stuff later in the pipe
                // and since we're blocking *that* based on the the resolution of this, we're better off just skipping
                // the message resoluion for things earlier in the pipeline. 
                ExecuteOnMainThread(() => {
                    result = MessageResolver(messageText, defaultText);
                    if (string.IsNullOrWhiteSpace(result)) {
                        result = null;
                    }
                    return true;
                }).Wait();
            }

            return result ?? Messages.ResourceManager.GetString(messageText) ?? defaultText;
        }

        public virtual bool AskPermission(string permission) {
#if DEBUG
            Message(Constants.Messages.NotImplemented, permission);
#endif
            return true;
        }

        public virtual IEnumerable<string> GetOptionValues(string key) {
            if (MyInvocation.BoundParameters.ContainsKey(key)) {
                var value = MyInvocation.BoundParameters[key];
                if (value is string || value is int) {
                    return new[] {
                        MyInvocation.BoundParameters[key].ToString()
                    };
                }
                if (value is SwitchParameter) {
                    return new[] {
                        ((SwitchParameter)MyInvocation.BoundParameters[key]).IsPresent.ToString()
                    };
                }
                if (value is string[]) {
                    return ((string[])value);
                }
                return new[] {
                    MyInvocation.BoundParameters[key].ToString()
                };
            }
            return DynamicParameterDictionary.Values.OfType<CustomRuntimeDefinedParameter>().Where(each => each.IsSet && each.Name == key).SelectMany(each => each.GetValues(this));
        }

        public virtual string CredentialUsername {
            get {
                return null;
            }
        }

        public virtual SecureString CredentialPassword {
            get {
                return null;
            }
        }

        public virtual bool ShouldContinueWithUntrustedPackageSource(string package, string packageSource) {
#if DEBUG
            Message(Constants.Messages.NotImplemented, packageSource);
#endif
            return true;
        }

        public virtual bool ShouldBootstrapProvider(string requestor, string providerName, string providerVersion, string providerType, string location, string destination) {
            try {
                if (Force || ForceBootstrap) {
                    return true;
                }

                return ShouldContinue(FormatMessageString(Constants.Messages.QueryBootstrap, providerName),
                    FormatMessageString(Constants.Messages.BootstrapProvider,
                        !string.IsNullOrWhiteSpace(requestor) ?
                            FormatMessageString(Constants.Messages.BootstrapProviderProviderRequested, requestor, providerName, providerVersion) :
                            FormatMessageString(Constants.Messages.BootstrapProviderUserRequested, providerName, providerVersion),
                        !string.IsNullOrWhiteSpace(providerType) && providerType.Equals(Constants.AssemblyProviderType) ?
                            FormatMessageString(Constants.Messages.BootstrapManualAssembly, providerName, location, destination) :
                            FormatMessageString(Constants.Messages.BootstrapManualInstall, providerName, location))).Result;
            } catch (Exception e) {
                e.Dump();
            }
            return false;
        }

        public virtual bool IsInteractive {
            get {
                return IsInvocation;
            }
        }

        public virtual int CallCount {
            get {
                return _callCount;
            }
        }

        #endregion

        protected override void Init() {
            if (!IsInitialized) {
                // get the service ( forces initialization )
                var x = PackageManagementService;
            }
        }

        protected IEnumerable<PackageProvider> SelectProviders(string[] names) {
            if (names.IsNullOrEmpty()) {
                return PackageManagementService.SelectProviders(null, this.SuppressErrorsAndWarnings(IsProcessing)).Where(each => !each.Features.ContainsKey(Microsoft.OneGet.Constants.Features.AutomationOnly));
            }
            // you can manually ask for any provider by name, if it is for automation only.
            if (IsInvocation) {
                return names.SelectMany(each => PackageManagementService.SelectProviders(each, this));
            }
            return names.SelectMany(each => PackageManagementService.SelectProviders(each, this.SuppressErrorsAndWarnings(IsProcessing)));
        }

        protected IEnumerable<PackageProvider> SelectProviders(string name) {
            // you can manually ask for any provider by name, if it is for automation only.
            if (IsInvocation) {
                var result = PackageManagementService.SelectProviders(name, this).ToArray();
                if (result.Length == 0) {
                    Warning(Constants.Errors.UnknownProvider, name);
                }
                return result;
            }

            var r = PackageManagementService.SelectProviders(name, this.SuppressErrorsAndWarnings(IsProcessing)).ToArray();
            if (r.Length == 0) {
                Warning(Constants.Errors.UnknownProvider, name);
            }
            return r;
        }

        public override bool ConsumeDynamicParameters() {
            // pull data from dynamic parameters and place them into the DynamicOptions collection.
            foreach (var rdp in DynamicParameterDictionary.Keys.Select(d => DynamicParameterDictionary[d]).Where(rdp => rdp.IsSet)) {
                if (rdp.ParameterType == typeof (SwitchParameter)) {
                    _dynamicOptions[rdp.Name] = true;
                } else {
                    _dynamicOptions[rdp.Name] = rdp.Value;
                }
            }
            return true;
        }
    }
}