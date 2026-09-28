"""账号写入请求共用模型；旧密码只作凭据验证，不套用新密码规则。"""

from typing import Annotated

from pydantic import AfterValidator, BaseModel, Field

from src.account_policy import validate_new_password, validate_username

NewPassword = Annotated[str, AfterValidator(validate_new_password)]
NewUsername = Annotated[str, AfterValidator(validate_username)]


class UserCreate(BaseModel):
    username: NewUsername
    password: NewPassword
    email: str | None = None
    group_ids: list[str] = Field(default_factory=list)


class PasswordChange(BaseModel):
    old_password: str
    new_password: NewPassword


class UserPasswordReset(BaseModel):
    new_password: NewPassword
    revoke_sessions: bool = True
