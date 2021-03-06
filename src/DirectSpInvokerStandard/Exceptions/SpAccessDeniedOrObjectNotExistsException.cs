﻿namespace DirectSp.Exceptions
{
    public class SpAccessDeniedOrObjectNotExistsException : DirectSpException
    {
        public SpAccessDeniedOrObjectNotExistsException(DirectSpException baseException) : base(baseException) { }
        public SpAccessDeniedOrObjectNotExistsException()
            : base(new SpCallError() { ErrorName = SpCommonExceptionId.AccessDeniedOrObjectNotExists.ToString(), ErrorId = (int)SpCommonExceptionId.AccessDeniedOrObjectNotExists }) { }
    }


}
