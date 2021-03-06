﻿using System;

namespace DirectSp.Exceptions
{
    internal static class DirectSpExceptionAdapter
    {
        public static DirectSpException Convert(Exception ex, CaptchaController captchaController)
        {
            if (ex is DirectSpException exception)
                return exception;

            //create invoker exception
            var ret = new DirectSpException(ex);

            // Convert to Sp* exceptions
            switch (ret.SpCallError.ErrorId)
            {
                case (int)SpCommonExceptionId.AccessDeniedOrObjectNotExists:
                    return new SpAccessDeniedOrObjectNotExistsException(ret);

                case (int)SpCommonExceptionId.InvalidCaptcha:
                    return new InvalidCaptchaException(captchaController.Create().Result, ret);

                case (int)SpCommonExceptionId.InvokerAppVersion:
                    return new SpInvokerAppVersionException(ret);

                case (int)SpCommonExceptionId.Maintenance:
                    return new SpMaintenanceException(ret);

                case (int)SpCommonExceptionId.MaintenanceReadOnly:
                    return new SpMaintenanceReadOnlyException(ret);

                case (int)SpCommonExceptionId.InvalidParamSignature:
                    return new SpInvalidParamSignatureException(ret);

                case (int)SpCommonExceptionId.ObjectAlreadyExists:
                    return new SpObjectAlreadyExists(ret);

                default:
                    return ret;
            }
        }
    }
}
