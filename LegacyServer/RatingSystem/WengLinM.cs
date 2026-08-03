using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;


namespace EcsServerLibM
{
	/// <summary>
	/// 레이팅 시스템에서 사용할 플레이어 인터페이스
	/// </summary>
	public interface IWengLinPlayerM
	{
		/// <summary>
		/// 플레이어의 현재 레이팅
		/// </summary>
		WengLinRatingM CurrentRating { get; set; }

		/// <summary>
		/// 플레이어 고유 식별자
		/// </summary>
		string PlayerId { get; }

		/// <summary>
		/// 기본 초기 레이팅 (μ = 1500, σ² = 350²)
		/// </summary>
		WengLinRatingM DefaultRating => new WengLinRatingM(1500.0, 350.0 * 350.0);

		/// <summary>
		/// 레이팅 업데이트
		/// </summary>
		/// <param name="newRating">새로운 레이팅</param>
		void UpdateRating(WengLinRatingM newRating) => CurrentRating = newRating;
	}

	/// <summary>
	/// 레이팅을 나타내는 구조체 (μ: 평균 스킬, σ²: 분산)
	/// </summary>
	public readonly struct WengLinRatingM : IEquatable<WengLinRatingM>
	{
		public readonly double Mean;           // μ (평균 스킬)
		public readonly double Variance;       // σ² (불확실성)

		public double StandardDeviation => Math.Sqrt(Variance);  // σ

		public WengLinRatingM(double mean, double variance)
		{
			Mean = mean;
			Variance = Math.Max(variance, WengLinM.MinVariance);
		}

		public WengLinRatingM(double mean, double variance, double conservativeMultiplier)
		{
			Mean = mean - conservativeMultiplier * Math.Sqrt(variance);
			Variance = Math.Max(variance, WengLinM.MinVariance);
		}

		public bool Equals(WengLinRatingM other) =>
			Math.Abs(Mean - other.Mean) < 1e-6 && Math.Abs(Variance - other.Variance) < 1e-6;

		public override bool Equals(object obj) => obj is WengLinRatingM other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(Mean, Variance);

		public static bool operator ==(WengLinRatingM left, WengLinRatingM right) => left.Equals(right);
		public static bool operator !=(WengLinRatingM left, WengLinRatingM right) => !left.Equals(right);

		public override string ToString() => $"μ={Mean:F2}, σ²={Variance:F2}, σ={StandardDeviation:F2}";
	}

	/// <summary>
	/// 게임 결과를 나타내는 열거형
	/// </summary>
	public enum eGameResultWengLin
	{
		Win = 1,
		Draw = 0,
		Loss = -1
	}

	/// <summary>
	/// 팀 정보를 담는 구조체
	/// </summary>
	public readonly struct TeamWengLinM
	{
		public readonly IWengLinPlayerM[] Players;
		public readonly int Rank;  // 1등, 2등, 3등... (낮을수록 좋음)

		public TeamWengLinM(IWengLinPlayerM[] players, int rank)
		{
			Players = players ?? throw new ArgumentNullException(nameof(players));
			Rank = rank;
		}

		public TeamWengLinM(IWengLinPlayerM player, int rank) : this(new[] { player }, rank) { }
	}

	/// <summary>
	/// WengLin 레이팅 시스템 구현
	/// </summary>
	public class WengLinM
	{
		// 시스템 상수들
		public const double MinVariance = 0.0001;
		public const double DefaultBeta = 175.0;  // 성능 불확실성
		public const double DefaultKappa = 0.0001; // 최소 분산 보호값

		private readonly double _beta;
		private readonly double _betaSquared;
		private readonly double _kappa;

		public WengLinM(double beta = DefaultBeta, double kappa = DefaultKappa)
		{
			_beta = beta;
			_betaSquared = beta * beta;
			_kappa = kappa;
		}

		/// <summary>
		/// 1대1 게임 업데이트
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void UpdateRatings(IWengLinPlayerM winner, IWengLinPlayerM loser)
		{
			var teams = new[]
			{
				new TeamWengLinM(winner, 1),  // 승자는 1등
                new TeamWengLinM(loser, 2)    // 패자는 2등
            };

			UpdateRatings(teams);
		}

		/// <summary>
		/// 1대1 무승부 업데이트
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void UpdateRatings(IWengLinPlayerM player1, IWengLinPlayerM player2, bool isDraw)
		{
			if (!isDraw)
				throw new ArgumentException("Use UpdateRatings(winner, loser) for non-draw games");

			var teams = new[]
			{
				new TeamWengLinM(player1, 1),  // 둘 다 1등 (동점)
                new TeamWengLinM(player2, 1)
			};

			UpdateRatings(teams);
		}

		/// <summary>
		/// 다중 팀 게임 업데이트 (메인 구현)
		/// </summary>
		public void UpdateRatings(TeamWengLinM[] teams)
		{
			if (teams == null || teams.Length < 2)
				throw new ArgumentException("At least 2 teams required");

			var teamCount = teams.Length;

			// 팀별 집계된 스킬 계산
			Span<double> teamMeans = stackalloc double[teamCount];
			Span<double> teamVariances = stackalloc double[teamCount];

			for (int i = 0; i < teamCount; i++)
			{
				CalculateTeamRating(teams[i].Players, out teamMeans[i], out teamVariances[i]);
			}

			// 각 팀에 대해 Ωi와 Δi 계산
			Span<double> omega = stackalloc double[teamCount];
			Span<double> delta = stackalloc double[teamCount];

			CalculateUpdates(teams, teamMeans, teamVariances, omega, delta);

			// 개별 플레이어 레이팅 업데이트
			for (int i = 0; i < teamCount; i++)
			{
				UpdateTeamPlayers(teams[i].Players, omega[i], delta[i]);
			}
		}

		/// <summary>
		/// 팀의 집계된 레이팅 계산
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void CalculateTeamRating(IWengLinPlayerM[] players, out double mean, out double variance)
		{
			mean = 0.0;
			variance = 0.0;

			foreach (var player in players)
			{
				mean += player.CurrentRating.Mean;
				variance += player.CurrentRating.Variance;
			}
		}

		/// <summary>
		/// Bradley-Terry 모델을 사용한 업데이트 값 계산
		/// </summary>
		private void CalculateUpdates(TeamWengLinM[] teams, Span<double> teamMeans, Span<double> teamVariances,
									Span<double> omega, Span<double> delta)
		{
			var teamCount = teams.Length;

			for (int i = 0; i < teamCount; i++)
			{
				omega[i] = 0.0;
				delta[i] = 0.0;

				for (int q = 0; q < teamCount; q++)
				{
					if (i == q) continue;

					// ciq 계산
					var ciq = Math.Sqrt(teamVariances[i] + teamVariances[q] + 2 * _betaSquared);

					// 예상 승률 계산
					var expDiff = Math.Exp((teamMeans[i] - teamMeans[q]) / ciq);
					var piq = expDiff / (1.0 + expDiff);

					// 실제 결과에 따른 점수
					double s = GetGameOutcomeScore(teams[i].Rank, teams[q].Rank);

					// Ωi 업데이트
					var deltaQ = (teamVariances[i] / ciq) * (s - piq);
					omega[i] += deltaQ;

					// Δi 업데이트  
					var etaQ = (teamVariances[i] / (ciq * ciq)) * piq * (1.0 - piq);
					delta[i] += etaQ;
				}
			}
		}

		/// <summary>
		/// 게임 결과에 따른 점수 반환
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static double GetGameOutcomeScore(int teamRank, int opponentRank)
		{
			if (teamRank < opponentRank) return 1.0;      // 승리
			if (teamRank > opponentRank) return 0.0;      // 패배
			return 0.5;                                   // 무승부
		}

		/// <summary>
		/// 팀 내 개별 플레이어들의 레이팅 업데이트
		/// </summary>
		private void UpdateTeamPlayers(IWengLinPlayerM[] players, double teamOmega, double teamDelta)
		{
			if (players.Length == 1)
			{
				// 단일 플레이어인 경우
				var player = players[0];
				var currentRating = player.CurrentRating;

				var newMean = currentRating.Mean + teamOmega;
				var newVariance = currentRating.Variance * Math.Max(1.0 - teamDelta, _kappa);

				player.UpdateRating(new WengLinRatingM(newMean, newVariance));
			}
			else
			{
				// 다중 플레이어인 경우 - 분산에 비례하여 배분
				double totalVariance = 0.0;
				foreach (var player in players)
				{
					totalVariance += player.CurrentRating.Variance;
				}

				foreach (var player in players)
				{
					var currentRating = player.CurrentRating;
					var varianceRatio = currentRating.Variance / totalVariance;

					var newMean = currentRating.Mean + varianceRatio * teamOmega;
					var newVariance = currentRating.Variance * Math.Max(1.0 - varianceRatio * teamDelta, _kappa);

					player.UpdateRating(new WengLinRatingM(newMean, newVariance));
				}
			}
		}

		/// <summary>
		/// 보수적인 레이팅 계산 (표시용)
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double GetConservativeRating(WengLinRatingM rating, double conservativeMultiplier = 3.0)
		{
			return rating.Mean - conservativeMultiplier * rating.StandardDeviation;
		}

		/// <summary>
		/// 매치 품질 계산 방법
		/// </summary>
		public enum MatchQualityMethod
		{
			/// <summary>
			/// 모든 팀 쌍의 품질을 계산하고 평균 (기본값)
			/// </summary>
			PairwiseAverage,

			/// <summary>
			/// 가중 평균 방식 (더 강한 팀들 간의 매치에 더 높은 가중치)
			/// </summary>
			WeightedAverage,

			/// <summary>
			/// 전체 분산 기반 방식
			/// </summary>
			VarianceBased,

			/// <summary>
			/// 엔트로피 기반 방식 (결과 불확실성)
			/// </summary>
			EntropyBased
		}

		/// <summary>
		/// 다중 팀 매치 품질 계산 (개선된 버전)
		/// </summary>
		public double CalculateMatchQuality(TeamWengLinM[] teams, MatchQualityMethod method = MatchQualityMethod.PairwiseAverage)
		{
			if (teams == null || teams.Length < 2)
				throw new ArgumentException("At least 2 teams required");

			return method switch
			{
				MatchQualityMethod.PairwiseAverage => CalculateMatchQualityPairwiseAverage(teams),
				MatchQualityMethod.WeightedAverage => CalculateMatchQualityWeightedAverage(teams),
				MatchQualityMethod.VarianceBased => CalculateMatchQualityVarianceBased(teams),
				MatchQualityMethod.EntropyBased => CalculateMatchQualityEntropyBased(teams),
				_ => throw new ArgumentOutOfRangeException(nameof(method))
			};
		}

		/// <summary>
		/// 방법 1: 모든 팀 쌍의 품질을 계산하고 평균
		/// </summary>
		private double CalculateMatchQualityPairwiseAverage(TeamWengLinM[] teams)
		{
			var teamCount = teams.Length;

			if (teamCount == 2)
			{
				// 2팀인 경우 기존 로직 사용
				return CalculateMatchQualityTwoTeams(teams[0], teams[1]);
			}

			// 모든 팀 쌍에 대해 품질 계산
			double totalQuality = 0.0;
			int pairCount = 0;

			for (int i = 0; i < teamCount; i++)
			{
				for (int j = i + 1; j < teamCount; j++)
				{
					totalQuality += CalculateMatchQualityTwoTeams(teams[i], teams[j]);
					pairCount++;
				}
			}

			return totalQuality / pairCount;
		}

		/// <summary>
		/// 방법 2: 가중 평균 방식 (더 균등한 팀들에게 더 높은 가중치)
		/// </summary>
		private double CalculateMatchQualityWeightedAverage(TeamWengLinM[] teams)
		{
			var teamCount = teams.Length;

			if (teamCount == 2)
			{
				return CalculateMatchQualityTwoTeams(teams[0], teams[1]);
			}

			// 팀별 레이팅 계산
			Span<double> teamMeans = stackalloc double[teamCount];
			Span<double> teamVariances = stackalloc double[teamCount];

			for (int i = 0; i < teamCount; i++)
			{
				CalculateTeamRating(teams[i].Players, out teamMeans[i], out teamVariances[i]);
			}

			double weightedQuality = 0.0;
			double totalWeight = 0.0;

			for (int i = 0; i < teamCount; i++)
			{
				for (int j = i + 1; j < teamCount; j++)
				{
					var pairQuality = CalculateMatchQualityTwoTeams(teams[i], teams[j]);

					// 팀들의 평균 스킬이 비슷할수록 더 높은 가중치
					var meanDiff = Math.Abs(teamMeans[i] - teamMeans[j]);
					var weight = Math.Exp(-meanDiff / (2 * _beta)); // 스킬 차이가 클수록 가중치 감소

					weightedQuality += pairQuality * weight;
					totalWeight += weight;
				}
			}

			return totalWeight > 0 ? weightedQuality / totalWeight : 0.0;
		}

		/// <summary>
		/// 방법 3: 전체 분산 기반 방식
		/// </summary>
		private double CalculateMatchQualityVarianceBased(TeamWengLinM[] teams)
		{
			var teamCount = teams.Length;

			// 팀별 레이팅 계산
			Span<double> teamMeans = stackalloc double[teamCount];
			Span<double> teamVariances = stackalloc double[teamCount];

			for (int i = 0; i < teamCount; i++)
			{
				CalculateTeamRating(teams[i].Players, out teamMeans[i], out teamVariances[i]);
			}

			// 전체 평균과 분산 계산
			double totalMean = 0.0;
			double totalVariance = 0.0;

			for (int i = 0; i < teamCount; i++)
			{
				totalMean += teamMeans[i];
				totalVariance += teamVariances[i];
			}

			totalMean /= teamCount;

			// 팀들 간의 스킬 차이 분산 계산
			double skillVariance = 0.0;
			for (int i = 0; i < teamCount; i++)
			{
				var diff = teamMeans[i] - totalMean;
				skillVariance += diff * diff;
			}
			skillVariance /= teamCount;

			// 게임 내 불확실성 대비 팀 간 스킬 차이의 비율
			var gameVariance = totalVariance / teamCount + _betaSquared;

			// 스킬 차이가 작고 게임 내 불확실성이 클수록 높은 품질
			return Math.Exp(-skillVariance / (2 * gameVariance)) / Math.Sqrt(2 * Math.PI * gameVariance);
		}

		/// <summary>
		/// 방법 4: 엔트로피 기반 방식 (결과 불확실성)
		/// </summary>
		private double CalculateMatchQualityEntropyBased(TeamWengLinM[] teams)
		{
			var teamCount = teams.Length;

			// 팀별 레이팅 계산
			Span<double> teamMeans = stackalloc double[teamCount];
			Span<double> teamVariances = stackalloc double[teamCount];

			for (int i = 0; i < teamCount; i++)
			{
				CalculateTeamRating(teams[i].Players, out teamMeans[i], out teamVariances[i]);
			}

			// 각 팀이 1등할 확률 계산 (Bradley-Terry 확장)
			Span<double> winProbabilities = stackalloc double[teamCount];
			double totalStrength = 0.0;

			for (int i = 0; i < teamCount; i++)
			{
				// 팀 강도를 평균 스킬의 지수로 계산
				var strength = Math.Exp(teamMeans[i] / _beta);
				winProbabilities[i] = strength;
				totalStrength += strength;
			}

			// 확률 정규화
			for (int i = 0; i < teamCount; i++)
			{
				winProbabilities[i] /= totalStrength;
			}

			// 엔트로피 계산 (높은 엔트로피 = 높은 불확실성 = 좋은 매치)
			double entropy = 0.0;
			for (int i = 0; i < teamCount; i++)
			{
				if (winProbabilities[i] > 0)
				{
					entropy -= winProbabilities[i] * Math.Log(winProbabilities[i]);
				}
			}

			// 최대 엔트로피로 정규화 (모든 팀이 동일한 확률일 때)
			var maxEntropy = Math.Log(teamCount);

			return maxEntropy > 0 ? entropy / maxEntropy : 0.0;
		}

		/// <summary>
		/// 2팀 간의 매치 품질 계산 (기존 로직)
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private double CalculateMatchQualityTwoTeams(TeamWengLinM team1, TeamWengLinM team2)
		{
			CalculateTeamRating(team1.Players, out var team1Mean, out var team1Variance);
			CalculateTeamRating(team2.Players, out var team2Mean, out var team2Variance);

			var totalVariance = team1Variance + team2Variance + 2 * _betaSquared;
			var meanDifference = team1Mean - team2Mean;

			return Math.Exp(-0.5 * meanDifference * meanDifference / totalVariance) / Math.Sqrt(2 * Math.PI * totalVariance);
		}

		/// <summary>
		/// 다중 팀 게임에서 각 팀의 승리 확률 예측
		/// </summary>
		public double[] PredictWinProbabilities(TeamWengLinM[] teams)
		{
			var teamCount = teams.Length;
			if (teamCount < 2)
				throw new ArgumentException("At least 2 teams required");

			// 팀별 레이팅 계산
			var teamMeans = new double[teamCount];
			var teamVariances = new double[teamCount];

			for (int i = 0; i < teamCount; i++)
			{
				CalculateTeamRating(teams[i].Players, out teamMeans[i], out teamVariances[i]);
			}

			// Bradley-Terry 모델을 이용한 승리 확률 계산
			var winProbabilities = new double[teamCount];
			double totalStrength = 0.0;

			for (int i = 0; i < teamCount; i++)
			{
				var strength = Math.Exp(teamMeans[i] / _beta);
				winProbabilities[i] = strength;
				totalStrength += strength;
			}

			// 확률 정규화
			for (int i = 0; i < teamCount; i++)
			{
				winProbabilities[i] /= totalStrength;
			}

			return winProbabilities;
		}

	}

	/// <summary>
	/// 간단한 플레이어 구현 예제
	/// </summary>
	public class Player : IWengLinPlayerM
	{
		public WengLinRatingM CurrentRating { get; set; }
		public string PlayerId { get; }

		public Player(string playerId, WengLinRatingM? initialRating = null)
		{
			PlayerId = playerId ?? throw new ArgumentNullException(nameof(playerId));
			CurrentRating = initialRating ?? ((IWengLinPlayerM)this).DefaultRating;
		}
	}
}

// 사용 예제
//public class Example
//{
//	public static void RunExample()
//	{
//		var ratingSystem = new WengLinRatingSystem();

//		// 플레이어 생성
//		var alice = new Player("Alice");
//		var bob = new Player("Bob");
//		var charlie = new Player("Charlie");
//		var david = new Player("David");

//		Console.WriteLine("=== 초기 레이팅 ===");
//		Console.WriteLine($"Alice: {alice.CurrentRating}");
//		Console.WriteLine($"Bob: {bob.CurrentRating}");
//		Console.WriteLine($"Charlie: {charlie.CurrentRating}");
//		Console.WriteLine($"David: {david.CurrentRating}");

//		// 1대1 매치: Alice가 Bob을 이김
//		Console.WriteLine("\n=== Alice가 Bob을 이김 ===");
//		ratingSystem.UpdateRatings(alice, bob);
//		Console.WriteLine($"Alice: {alice.CurrentRating}");
//		Console.WriteLine($"Bob: {bob.CurrentRating}");

//		// 팀 매치: Alice+Charlie 팀이 Bob+David 팀을 이김
//		Console.WriteLine("\n=== 팀 매치: Alice+Charlie가 Bob+David를 이김 ===");
//		var teams = new[]
//		{
//			new TeamWengLinM(new[] { alice, charlie }, 1),  // 승리팀
//            new TeamWengLinM(new[] { bob, david }, 2)       // 패배팀
//        };

//		ratingSystem.UpdateRatings(teams);

//		Console.WriteLine($"Alice: {alice.CurrentRating}");
//		Console.WriteLine($"Bob: {bob.CurrentRating}");
//		Console.WriteLine($"Charlie: {charlie.CurrentRating}");
//		Console.WriteLine($"David: {david.CurrentRating}");

//		// 매치 품질 계산
//		var matchQuality = ratingSystem.CalculateMatchQuality(teams);
//		Console.WriteLine($"\n매치 품질: {matchQuality:F4}");

//		// 보수적인 레이팅 표시
//		Console.WriteLine("\n=== 보수적인 레이팅 (표시용) ===");
//		Console.WriteLine($"Alice: {WengLinRatingSystem.GetConservativeRating(alice.CurrentRating):F0}");
//		Console.WriteLine($"Bob: {WengLinRatingSystem.GetConservativeRating(bob.CurrentRating):F0}");
//		Console.WriteLine($"Charlie: {WengLinRatingSystem.GetConservativeRating(charlie.CurrentRating):F0}");
//		Console.WriteLine($"David: {WengLinRatingSystem.GetConservativeRating(david.CurrentRating):F0}");
//	}
//}
