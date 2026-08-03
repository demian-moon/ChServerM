using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EcsServerLibM
{
	public interface IGlickoRatingM
	{
		string Name { get; }		
		double _rating { get; set; }		
		double _deviation { get; set; } // 신뢰도 값이 크면 불확실성이 크다는 의미, 게임 결과가 레이팅에 더 큰 영향을 미침 (게임수, 시간 - 게임 가중치 결정)

		// Tau값은 플레이어의 volatility가 한번에 얼마나 크게 변할 수 있는지 결정하는 값
		double _volatility { get; set; } // 성과의 일관성, 레이팅 변동성 - 값이 낮으면 일관된 수준, 값이 높으면 변동성이 커짐(갑자기 잘하거나, 못할 때)
		DateTime LastPlayTime { get; set; }

		// 안전한 값 반환 (기본 구현 제공)
		double Rating
		{
			get => _rating > 0 ? _rating : Glicko2M.DefaultRating;
			set => _rating = value;
		}

		double Deviation
		{
			get => _deviation > 0 ? _deviation : Glicko2M.DefaultDeviation;
			set => _deviation = value;
		}

		double Volatility
		{
			get => _volatility > 0 ? Math.Min(_volatility, 1.0) : Glicko2M.DefaultVolatility;
			set => _volatility = value;
		}
	}

	public static class Glicko2M
	{
		// 상수들
		public static double DefaultRating = 1500.0;
		public static double DefaultDeviation = 350.0;
		public static double DefaultVolatility = 0.06;
		private const double DefaultTau = 0.75; // 변동성이 크도록 설정하려면 1.2 이하로 설정가능
		private const double Multiplier = 173.7178;
		private const double InverseMultiplier = 0.005757101449275362; // (1 / Multiplier)
		private const double ConvergeTol = 0.000_001;
		private const double PiSqOver3 = Math.PI * Math.PI / 3.0; // 미리 계산
		private const double TauMax = 1.2;
		private const double TauPerDay = 0.5;
		private const double VolMax = 1.0;

		public enum E_MatchResult : int { Draw = 0, Lose = 1, Win = 2 }

		private readonly struct GlickoResult
		{
			public readonly E_MatchResult eWinOrLose;
			public readonly double oppTeamAveRating;
			public readonly double oppTeamAveDeviation;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public GlickoResult(E_MatchResult eWinOrLose, double oppTeamAveRating, double oppTeamAveDeviation)
			{
				this.eWinOrLose = eWinOrLose;
				this.oppTeamAveRating = oppTeamAveRating;
				this.oppTeamAveDeviation = oppTeamAveDeviation;
			}

			public double Score
			{
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				get => eWinOrLose switch
				{
					E_MatchResult.Win => 1.0,
					E_MatchResult.Draw => 0.5,
					E_MatchResult.Lose => 0.0,
					_ => throw new ArgumentOutOfRangeException(nameof(eWinOrLose), "Invalid match result code.")
				};
			}
		}

		// 개인전
		public static void UpdateRatings(IGlickoRatingM winner, IGlickoRatingM loser, bool isDraw)
		{
			if (winner == null || loser == null) return; // null 안전성 추가

			var winPool = new List<IGlickoRatingM>(1) { winner }; // 용량 미리 설정
			var losePool = new List<IGlickoRatingM>(1) { loser };
			UpdateRatings(winPool, losePool, isDraw);
		}

		// 팀전
		public static void UpdateRatings(List<IGlickoRatingM> winTeam, List<IGlickoRatingM> loseTeam, bool isDraw)
		{
			if (winTeam == null || loseTeam == null || winTeam.Count == 0 || loseTeam.Count == 0)
				return; // 빈 팀 방어 강화

			var winWL = isDraw ? E_MatchResult.Draw : E_MatchResult.Win;
			var loseWL = isDraw ? E_MatchResult.Draw : E_MatchResult.Lose;

			// 상대 팀 평균 계산
			var oppWin = BuildResult(loseTeam, loseWL);
			var oppLose = BuildResult(winTeam, winWL);

			// 병렬 처리 가능한 구조로 개별 유저 갱신
			CalcNewRatingTeam(winTeam, oppWin);
			CalcNewRatingTeam(loseTeam, oppLose);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static GlickoResult BuildResult(List<IGlickoRatingM> team, E_MatchResult wl)
		{
			double sumRating = 0.0;
			double sumDeviation = 0.0;
			int activeCount = 0;

			// foreach가 for보다 약간 더 효율적
			foreach (var user in team)
			{
				if (user == null) continue;
				InitDefaults(user);
				sumRating += ScaleRating(user.Rating);
				sumDeviation += ScaleDeviation(user.Deviation);
				++activeCount;
			}

			// 빈 팀 방어
			return activeCount == 0
				? new GlickoResult(wl, 0.0, ScaleDeviation(DefaultDeviation))
				: new GlickoResult(wl, sumRating / activeCount, sumDeviation / activeCount);
		}

		private static void CalcNewRatingTeam(List<IGlickoRatingM> team, GlickoResult res)
		{
			foreach (var user in team)
			{
				if (user == null) continue;
				InitDefaults(user);
				double tau = CalcTau(user);
				CalcNewRating(user, res, tau);
				user.LastPlayTime = DateTime.UtcNow;
			}
		}

		private static void CalcNewRating(IGlickoRatingM user, GlickoResult gr, double tau)
		{
			// 스케일 변환
			double mu = ScaleRating(user.Rating);
			double phi = ScaleDeviation(user.Deviation);
			double sigma = user.Volatility;

			// v, Δ 계산
			double g = G(gr.oppTeamAveDeviation);
			double E = Eout(mu, gr.oppTeamAveRating, g);
			double v = 1.0 / (g * g * E * (1.0 - E));
			double delta = v * g * (gr.Score - E);

			// 새로운 σ 계산
			double newSigma = SolveSigma(phi, sigma, delta, v, tau);

			// RD* → φ'
			double phiSqPlusNewSigmaSq = phi * phi + newSigma * newSigma;
			double phiPrime = 1.0 / Math.Sqrt(1.0 / phiSqPlusNewSigmaSq + 1.0 / v);

			// μ'
			double muPrime = mu + phiPrime * phiPrime * g * (gr.Score - E);

			// 원래 스케일로 복귀
			user.Volatility = Math.Min(newSigma, VolMax); // 상한 제한
			user.Deviation = OriginDeviation(phiPrime);
			user.Rating = OriginRating(muPrime);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double CalcTau(IGlickoRatingM user)
		{
			var timeSpan = DateTime.UtcNow - user.LastPlayTime;
			int days = Math.Max(0, timeSpan.Days);
			// 시간에 따른 tau 증가 - 필요에 따라 조정 가능
			return Math.Min(DefaultTau + 0.03 * days, TauMax);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void InitDefaults(IGlickoRatingM user)
		{
			if (user.Rating <= 0) user.Rating = DefaultRating;
			if (user.Deviation <= 0) user.Deviation = DefaultDeviation;
			if (user.Volatility <= 0) user.Volatility = DefaultVolatility;
			if (user.Volatility > VolMax) user.Volatility = VolMax;
		}

		// g(φ) - Math.Pow 제거
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double G(double phi)
		{
			double phiSq = phi * phi;
			return 1.0 / Math.Sqrt(1.0 + PiSqOver3 * phiSq);
		}

		// E(μ, μj, φj)
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double Eout(double mu, double muJ, double g) =>
			1.0 / (1.0 + Math.Exp(-g * (mu - muJ)));

		// σ' 계산 - 수치 안정성 개선 및 버그 수정
		private static double SolveSigma(double phi, double sigma, double delta, double v, double tau)
		{
			double sigmaSq = sigma * sigma;
			double phiSq = phi * phi;
			double tauSq = tau * tau;
			double deltaSq = delta * delta;

			double a = Math.Log(sigmaSq);
			double A = a;
			double B;

			if (deltaSq > phiSq + v)
			{
				B = Math.Log(deltaSq - phiSq - v);
			}
			else
			{
				// 수정된 B 초기화 로직
				int k = 1;
				do
				{
					B = a - k * tau;
					k++;
				} while (F(B) < 0 && k <= 100); // 조건 수정

				// 안전성 체크 추가
				if (F(B) < 0)
				{
					B = a - 100 * tau; // fallback 값
				}
			}

			double fA = F(A);
			double fB = F(B);

			const int maxIterations = 10000; // 상수로 변경
			int iterations = 0;

			// 수렴 조건 및 안전장치 강화
			while (Math.Abs(B - A) > ConvergeTol && iterations < maxIterations)
			{
				double C = A + (A - B) * fA / (fB - fA);
				double fC = F(C);

				if (fC * fB < 0)
				{
					A = B; fA = fB;
				}
				else
				{
					fA *= 0.5;
				}

				B = C; fB = fC;
				iterations++;
			}

			// 수렴 실패 시 로그 또는 처리 가능
			if (iterations >= maxIterations)
			{
				// 필요시 로깅 또는 예외 처리
				ServerM.logM.Debug($"SolveSigma did not converge after {maxIterations} iterations");
			}

			double result = Math.Exp(A * 0.5);
			return Math.Min(result, VolMax); // 상한 제한 추가

			// 내부 F(x) - Math.Pow 제거
			double F(double x)
			{
				double ex = Math.Exp(x);
				double phiSqPlusVPlusEx = phiSq + v + ex;
				double num = ex * (deltaSq - phiSq - v - ex);
				double denom = 2.0 * phiSqPlusVPlusEx * phiSqPlusVPlusEx; // Math.Pow 제거
				return num / denom - (x - a) / tauSq;
			}
		}

		// 스케일 변환 - 나눗셈을 곱셈으로 최적화
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double ScaleRating(double rating) =>
			(rating - DefaultRating) * InverseMultiplier;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double ScaleDeviation(double dev) =>
			dev * InverseMultiplier;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double OriginRating(double mu) =>
			mu * Multiplier + DefaultRating;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double OriginDeviation(double phi) =>
			phi * Multiplier;
	}
}

