using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	internal class AuthM
	{
		static public bool IsPassed(string rawPw, string hashedPw)
		{
			var hasher = new PasswordHasher<object>();

			// 로그인 시 검증
			var result = hasher.VerifyHashedPassword(
							user: null,
							hashedPassword: hashedPw,
							providedPassword: rawPw);

			return result == PasswordVerificationResult.Success;
		}

		static public string GetHashPassword(string rawPw)
		{
			var hasher = new PasswordHasher<object>();
			// 회원가입 시 비밀번호 해싱
			var hashedPw = hasher.HashPassword(
							user: null,
							password: rawPw);
			return hashedPw;
		}

	}
}
