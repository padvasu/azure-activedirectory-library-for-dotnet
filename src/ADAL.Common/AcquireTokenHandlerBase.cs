﻿//----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//----------------------------------------------------------------------

using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{
    internal abstract class AcquireTokenHandlerBase
    {
        protected const string NullResource = "null_resource_as_optional";
        protected readonly static Task CompletedTask = Task.FromResult(false);
        private readonly TokenCache tokenCache;

        protected AcquireTokenHandlerBase(Authenticator authenticator, TokenCache tokenCache, string resource, ClientKey clientKey, TokenSubjectType subjectType, bool callSync)
        {
            this.Authenticator = authenticator;

            this.tokenCache = tokenCache;

            if (string.IsNullOrWhiteSpace(resource))
            {
                throw new ArgumentNullException("resource");
            }

            this.Resource = (resource != NullResource) ? resource : null;
            this.ClientKey = clientKey;
            this.TokenSubjectType = subjectType;
            this.CallState = this.CreateCallState(callSync);

            this.LoadFromCache = (tokenCache != null);
            this.StoreToCache = (tokenCache != null);
            this.SupportADFS = false;
        }

        internal CallState CallState { get; set; }

        protected bool SupportADFS { get; set; }

        protected Authenticator Authenticator { get; private set; }

        protected string Resource { get; set; }

        protected ClientKey ClientKey { get; private set; }

        protected TokenSubjectType TokenSubjectType { get; private set; }

        protected string UniqueId { get; set; }

        protected string DisplayableId { get; set; }

        protected UserIdentifierType UserIdentifierType { get; set; }

        protected bool LoadFromCache { get; set; }
        
        protected bool StoreToCache { get; set; }

        public async Task<AuthenticationResult> RunAsync()
        {
            await this.Authenticator.UpdateFromMetadataAsync(this.CallState);
            this.ValidateAuthorityType();

            await SetUserDisplayableId();

            bool notifiedBeforeAccessCache = false;

            try
            {
                AuthenticationResult result = null;
                if (this.LoadFromCache)
                { 
                    this.NotifyBeforeAccessCache();
                    notifiedBeforeAccessCache = true;

                    result = this.tokenCache.LoadFromCache(this.Authenticator.Authority, this.Resource, this.ClientKey.ClientId, this.TokenSubjectType, this.UniqueId, this.DisplayableId, this.CallState);
                    if (result != null && result.AccessToken == null && result.RefreshToken != null)
                    {
                        result = await this.RefreshAccessTokenAsync(result);
                        if (result != null)
                        {
                            this.tokenCache.StoreToCache(result, this.Authenticator.Authority, this.Resource, this.ClientKey.ClientId, this.TokenSubjectType);
                        }
                    }
                }
                
                if (result == null)
                {
                    await this.PreTokenRequest();
                    result = await this.SendTokenRequestAsync();
                    this.PostTokenRequest(result);

                    await this.Authenticator.UpdateAuthorityTenantAsync(result.TenantId, this.CallState);

                    if (this.StoreToCache)
                    {
                        if (!notifiedBeforeAccessCache)
                        {
                            this.NotifyBeforeAccessCache();
                            notifiedBeforeAccessCache = true;
                        }

                        this.tokenCache.StoreToCache(result, this.Authenticator.Authority, this.Resource, this.ClientKey.ClientId, this.TokenSubjectType);
                    }
                }

                LogReturnedToken(result);
                return result;
            }
            finally
            {
                if (notifiedBeforeAccessCache)
                {
                    this.NotifyAfterAccessCache();
                }
            }
        }

        protected virtual Task PreTokenRequest()
        {
            return CompletedTask;
        }

        protected virtual void PostTokenRequest(AuthenticationResult result)
        {
        }

        protected abstract void AddAditionalRequestParameters(RequestParameters requestParameters);

        protected virtual Task SetUserDisplayableId()
        {
            return CompletedTask;
        }

        protected virtual async Task<AuthenticationResult> SendTokenRequestAsync()
        {
            RequestParameters requestParameters = new RequestParameters(this.Resource, this.ClientKey, this.Authenticator.SelfSignedJwtAudience);
            this.AddAditionalRequestParameters(requestParameters);
            return await this.SendHttpMessageAsync(requestParameters);
        }

        private async Task<AuthenticationResult> RefreshAccessTokenAsync(AuthenticationResult result)
        {
            AuthenticationResult newResult = null;

            if (this.Resource != null)
            {
                try
                {
                    newResult = await this.SendTokenRequestByRefreshTokenAsync(result.RefreshToken);
                    await this.Authenticator.UpdateAuthorityTenantAsync(result.TenantId, this.CallState);

                    if (newResult.IdToken == null)
                    {
                        // If Id token is not returned by token endpoint when refresh token is redeemed, we should copy tenant and user information from the cached token.
                        newResult.UpdateTenantAndUserInfo(result.TenantId, result.IdToken, result.UserInfo);
                    }
                }
                catch (AdalException ex)
                {
                    AdalServiceException serviceException = ex as AdalServiceException;
                    if (serviceException != null && serviceException.ErrorCode == "invalid_request")
                    {
                        throw new AdalServiceException(
                            AdalError.FailedToRefreshToken,
                            AdalErrorMessage.FailedToRefreshToken + ". " + serviceException.Message,
                            (WebException)serviceException.InnerException);
                    }

                    newResult = null;
                }
            }

            return newResult;
        }

        protected async Task<AuthenticationResult> SendTokenRequestByRefreshTokenAsync(string refreshToken)
        {
            RequestParameters requestParameters = new RequestParameters(this.Resource, this.ClientKey, this.Authenticator.SelfSignedJwtAudience);
            requestParameters[OAuthParameter.GrantType] = OAuthGrantType.RefreshToken;
            requestParameters[OAuthParameter.RefreshToken] = refreshToken;
            AuthenticationResult result = await this.SendHttpMessageAsync(requestParameters);

            if (result.RefreshToken == null)
            {
                result.RefreshToken = refreshToken;
            }

            return result;
        }

        private async Task<AuthenticationResult> SendHttpMessageAsync(RequestParameters requestParameters)
        {
            string uri = HttpHelper.CheckForExtraQueryParameter(this.Authenticator.TokenUri);

            TokenResponse tokenResponse = await HttpHelper.SendPostRequestAndDeserializeJsonResponseAsync<TokenResponse>(uri, requestParameters, this.CallState);

            return OAuth2Response.ParseTokenResponse(tokenResponse);
        }

        private void NotifyBeforeAccessCache()
        {
            this.tokenCache.OnBeforeAccess(new TokenCacheNotificationArgs
            {
                TokenCache = this.tokenCache,
                Resource = this.Resource,
                ClientId = this.ClientKey.ClientId,
                UniqueId = this.UniqueId,
                DisplayableId = this.DisplayableId
            });
        }

        private void NotifyAfterAccessCache()
        {
            this.tokenCache.OnAfterAccess(new TokenCacheNotificationArgs
            {
                TokenCache = this.tokenCache,
                Resource = this.Resource,
                ClientId = this.ClientKey.ClientId,
                UniqueId = this.UniqueId,
                DisplayableId = this.DisplayableId
            });
        }

        private void LogReturnedToken(AuthenticationResult result)
        {
            if (result.AccessToken != null)
            {
                string accessTokenHash = PlatformSpecificHelper.CreateSha256Hash(result.AccessToken);
                string logMessage;
                if (result.RefreshToken != null)
                {
                    string refreshTokenHash = PlatformSpecificHelper.CreateSha256Hash(result.RefreshToken);
                    logMessage = string.Format("Access Token with hash '{0}' and Refresh Token with hash '{1}' returned", accessTokenHash, refreshTokenHash);
                }
                else
                {
                    logMessage = string.Format("Access Token with hash '{0}' returned", accessTokenHash);
                }

                Logger.Verbose(this.CallState, logMessage);
            }
        }

        private void ValidateAuthorityType()
        {
            if (!this.SupportADFS && this.Authenticator.AuthorityType == AuthorityType.ADFS)
            {
                Logger.Error(this.CallState, "Invalid authority type '{0}'", this.Authenticator.AuthorityType);
                throw new AdalException(AdalError.InvalidAuthorityType,
                    string.Format(CultureInfo.InvariantCulture, AdalErrorMessage.InvalidAuthorityTypeTemplate, this.Authenticator.Authority));
            }
        }

        private CallState CreateCallState(bool callSync)
        {
            Guid correlationId = (this.Authenticator.CorrelationId != Guid.Empty) ? this.Authenticator.CorrelationId : Guid.NewGuid();
            return new CallState(correlationId, callSync);
        }
    }
}
